using System.IO;
using UnityEngine;

namespace LocalModels.VoxelBridge
{
    public enum VoxelSourceAxes
    {
        [InspectorName("Preserve Local Axes")] PreserveLocalAxes = 0,
        [InspectorName("Z Up → Y Up")] ZUp = 1
    }

    internal static class VoxelSourceOrientation
    {
        internal static void Validate(VoxelSourceAxes axes)
        {
            if (axes != VoxelSourceAxes.PreserveLocalAxes && axes != VoxelSourceAxes.ZUp)
                throw new InvalidDataException("Unknown source axes configuration.");
        }

        internal static Matrix4x4 ToUnity(VoxelSourceAxes axes)
        {
            Validate(axes);
            var matrix = Matrix4x4.identity;
            if (axes == VoxelSourceAxes.ZUp)
            {
                // Exact quarter turn avoids rounding errors at voxel grid boundaries.
                matrix.m11 = 0; matrix.m12 = 1;
                matrix.m21 = -1; matrix.m22 = 0;
            }
            return matrix;
        }

        internal static void CopyPlacement(Transform source, Transform destination, VoxelSourceAxes axes)
        {
            Validate(axes);
            destination.localPosition = source.localPosition;
            destination.localRotation = axes == VoxelSourceAxes.ZUp
                ? source.localRotation * Quaternion.Euler(90, 0, 0) : source.localRotation;
            Vector3 scale = source.localScale;
            destination.localScale = axes == VoxelSourceAxes.ZUp
                ? new Vector3(scale.x, scale.z, scale.y) : scale;
        }

        internal static VoxelSourceAxes ReadPlacementAxes(string manifestPath)
        {
            if (!VoxelLodPipeline.TryReadManifest(manifestPath, out var manifest))
                throw new InvalidDataException("A valid family manifest is required for scene placement.");
            Validate(manifest.sourceAxes);
            return manifest.sourceAxes;
        }
    }
}
