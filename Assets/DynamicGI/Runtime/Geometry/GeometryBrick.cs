using System;
using UnityEngine;

namespace DynamicGI.Geometry
{
    /// <summary>
    /// CPU-side metadata for one sparse geometry brick. Voxel payload stays on the GPU.
    /// </summary>
    [Serializable]
    public sealed class GeometryBrick
    {
        public Vector3Int Coordinate { get; }
        public int Slot { get; }
        public Bounds WorldBounds { get; internal set; }
        public bool IsDirty { get; internal set; }
        public int LastRebuiltFrame { get; internal set; } = -1;

        internal GeometryBrick(Vector3Int coordinate, int slot, Bounds worldBounds)
        {
            Coordinate = coordinate;
            Slot = slot;
            WorldBounds = worldBounds;
            IsDirty = true;
        }
    }

    public enum GeometryContributionType
    {
        Base = 0,
        Dynamic = 1
    }

    public enum GeometryCollectionMode
    {
        ExplicitContributorsOnly = 0,
        LayerMaskOnly = 1,
        ExplicitContributorsAndLayerMask = 2
    }

    public readonly struct GeometryOccupancyResult
    {
        public readonly Vector3 WorldPosition;
        public readonly bool IsOccupied;
        public readonly bool HasError;

        public GeometryOccupancyResult(Vector3 worldPosition, bool isOccupied, bool hasError)
        {
            WorldPosition = worldPosition;
            IsOccupied = isOccupied;
            HasError = hasError;
        }
    }

    public readonly struct GeometryFieldStats
    {
        public readonly int ActiveBricks;
        public readonly int DirtyBricks;
        public readonly int UpdatedBricksThisFrame;
        public readonly int ComputeDispatchesThisFrame;
        public readonly long EstimatedGpuBytes;
        public readonly double UpdateCpuMilliseconds;

        public GeometryFieldStats(
            int activeBricks,
            int dirtyBricks,
            int updatedBricksThisFrame,
            int computeDispatchesThisFrame,
            long estimatedGpuBytes,
            double updateCpuMilliseconds)
        {
            ActiveBricks = activeBricks;
            DirtyBricks = dirtyBricks;
            UpdatedBricksThisFrame = updatedBricksThisFrame;
            ComputeDispatchesThisFrame = computeDispatchesThisFrame;
            EstimatedGpuBytes = estimatedGpuBytes;
            UpdateCpuMilliseconds = updateCpuMilliseconds;
        }
    }
}
