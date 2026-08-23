using System.Runtime.InteropServices;
using UnityEngine;

namespace DynamicGI.Radiance
{
    public enum RadianceDebugDirection
    {
        Average = 0,
        PositiveX = 1,
        NegativeX = 2,
        PositiveY = 3,
        NegativeY = 4,
        PositiveZ = 5,
        NegativeZ = 6,
        Maximum = 7
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RadianceProbeGpuData
    {
        public const int Stride = sizeof(float) * 4 * 6;

        public Vector4 PositiveX;
        public Vector4 NegativeX;
        public Vector4 PositiveY;
        public Vector4 NegativeY;
        public Vector4 PositiveZ;
        public Vector4 NegativeZ;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RadianceDebugSampleGpu
    {
        public const int Stride = sizeof(float) * 8;

        public Vector4 PositionAndLuminance;
        public Vector4 ColorAndValidity;
    }

    public readonly struct RadianceProbeResult
    {
        public readonly Vector3 WorldPosition;
        public readonly RadianceProbeGpuData Radiance;
        public readonly bool HasError;

        public RadianceProbeResult(Vector3 worldPosition, RadianceProbeGpuData radiance, bool hasError)
        {
            WorldPosition = worldPosition;
            Radiance = radiance;
            HasError = hasError;
        }

        public Vector3 Average =>
            (ToVector3(Radiance.PositiveX) + ToVector3(Radiance.NegativeX) +
             ToVector3(Radiance.PositiveY) + ToVector3(Radiance.NegativeY) +
             ToVector3(Radiance.PositiveZ) + ToVector3(Radiance.NegativeZ)) / 6f;

        public float AverageLuminance
        {
            get
            {
                Vector3 value = Average;
                return Vector3.Dot(value, new Vector3(0.2126f, 0.7152f, 0.0722f));
            }
        }

        private static Vector3 ToVector3(Vector4 value) => new(value.x, value.y, value.z);
    }

    public readonly struct RadianceFieldStats
    {
        public readonly Vector3Int Resolution;
        public readonly int ProbeCount;
        public readonly int TotalTiles;
        public readonly int DirtyTiles;
        public readonly int UpdatedTilesThisFrame;
        public readonly int UpdatedProbesThisFrame;
        public readonly int ComputeDispatchesThisFrame;
        public readonly long EstimatedGpuBytes;
        public readonly double UpdateCpuMilliseconds;

        public RadianceFieldStats(
            Vector3Int resolution,
            int probeCount,
            int totalTiles,
            int dirtyTiles,
            int updatedTilesThisFrame,
            int updatedProbesThisFrame,
            int computeDispatchesThisFrame,
            long estimatedGpuBytes,
            double updateCpuMilliseconds)
        {
            Resolution = resolution;
            ProbeCount = probeCount;
            TotalTiles = totalTiles;
            DirtyTiles = dirtyTiles;
            UpdatedTilesThisFrame = updatedTilesThisFrame;
            UpdatedProbesThisFrame = updatedProbesThisFrame;
            ComputeDispatchesThisFrame = computeDispatchesThisFrame;
            EstimatedGpuBytes = estimatedGpuBytes;
            UpdateCpuMilliseconds = updateCpuMilliseconds;
        }
    }
}
