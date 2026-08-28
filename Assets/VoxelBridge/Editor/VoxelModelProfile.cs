using System;
using UnityEngine;

namespace LocalModels.VoxelBridge
{
    [CreateAssetMenu(fileName = "VoxelStyleProfile", menuName = "Voxel Bridge/Perfil de estilo voxel")]
    public sealed class VoxelStyleProfile : ScriptableObject
    {
        [Tooltip("Tamaño de un vóxel de LOD0 en unidades de Unity (0.1 equivale a 10 cm).")]
        [SerializeField, Min(0.001f)] private float baseVoxelSize = 0.1f;
        [Tooltip("Escala de celda de cada LOD. Debe empezar en 1 y usar potencias de dos crecientes.")]
        [SerializeField] private int[] lodMultipliers = { 1, 2, 4 };
        [Tooltip("Máximo de celdas por eje de cada modelo interno del archivo VOX.")]
        [SerializeField, Range(16, 256)] private int chunkCellSize = 128;
        [Tooltip("Celdas vacías añadidas alrededor del volumen fuente.")]
        [SerializeField, Range(0, 8)] private int padding = 1;
        [Tooltip("Rellena volúmenes cerrados para que no conserven caras interiores.")]
        [SerializeField] private bool fillInterior = true;
        [Tooltip("Activa Ignore Cavity al importar mediante Voxel Importer.")]
        [SerializeField] private bool hideInternalCavities = true;
        [Tooltip("Altura relativa de pantalla de transición para cada nivel del LODGroup.")]
        [SerializeField] private float[] lodScreenHeights = { 0.6f, 0.3f, 0.1f };

        public float BaseVoxelSize => Mathf.Max(0.001f, baseVoxelSize);
        public int ChunkCellSize => Mathf.Clamp(chunkCellSize, 16, 256);
        public int Padding => Mathf.Clamp(padding, 0, 8);
        public bool FillInterior => fillInterior;
        public bool HideInternalCavities => hideInternalCavities;
        public int LodCount => lodMultipliers?.Length ?? 0;

        public int GetLodMultiplier(int index)
        {
            if (lodMultipliers == null || index < 0 || index >= lodMultipliers.Length)
                throw new ArgumentOutOfRangeException(nameof(index));
            return Mathf.Max(1, lodMultipliers[index]);
        }

        public float GetLodScreenHeight(int index)
        {
            if (lodScreenHeights != null && index >= 0 && index < lodScreenHeights.Length)
                return Mathf.Clamp01(lodScreenHeights[index]);
            return Mathf.Max(0.01f, 0.6f * Mathf.Pow(0.5f, index));
        }

        public bool TryValidate(out string error)
        {
            if (baseVoxelSize <= 0f || float.IsNaN(baseVoxelSize) || float.IsInfinity(baseVoxelSize))
            {
                error = "La unidad base debe ser mayor que cero.";
                return false;
            }
            if (lodMultipliers == null || lodMultipliers.Length == 0 || lodMultipliers.Length > 8)
            {
                error = "El perfil debe contener entre 1 y 8 niveles LOD.";
                return false;
            }
            if (lodMultipliers[0] != 1)
            {
                error = "LOD0 debe usar multiplicador 1 para representar la unidad voxel base.";
                return false;
            }

            int previous = 0;
            float previousHeight = 1.01f;
            for (int i = 0; i < lodMultipliers.Length; i++)
            {
                int multiplier = lodMultipliers[i];
                if (multiplier < 1 || (multiplier & (multiplier - 1)) != 0 || multiplier <= previous)
                {
                    error = "Los multiplicadores LOD deben ser potencias de dos estrictamente crecientes.";
                    return false;
                }
                previous = multiplier;

                float height = GetLodScreenHeight(i);
                if (height <= 0f || height >= previousHeight)
                {
                    error = "Las transiciones LOD deben ser mayores que cero y estrictamente descendentes.";
                    return false;
                }
                previousHeight = height;
            }
            error = null;
            return true;
        }
    }

    internal readonly struct VoxelGridPlan
    {
        public readonly float VoxelSize;
        public readonly Vector3 Origin;
        public readonly Vector3Int Size;
        public readonly Bounds SourceBounds;
        public readonly int ChunkCellSize;

        public VoxelGridPlan(float voxelSize, Vector3 origin, Vector3Int size,
            Bounds sourceBounds, int chunkCellSize)
        {
            VoxelSize = voxelSize;
            Origin = origin;
            Size = size;
            SourceBounds = sourceBounds;
            ChunkCellSize = chunkCellSize;
        }

        public long CellCount => (long)Size.x * Size.y * Size.z;
        public Vector3Int ChunkCounts => new(
            Mathf.CeilToInt((float)Size.x / ChunkCellSize),
            Mathf.CeilToInt((float)Size.y / ChunkCellSize),
            Mathf.CeilToInt((float)Size.z / ChunkCellSize));
    }

    internal static class VoxelGridPlanner
    {
        internal const long MaximumDenseCellCount = 80_000_000;

        public static VoxelGridPlan Create(Bounds bounds, float voxelSize, int padding, int chunkCellSize)
        {
            if (voxelSize <= 0f || float.IsNaN(voxelSize) || float.IsInfinity(voxelSize))
                throw new ArgumentOutOfRangeException(nameof(voxelSize));
            padding = Mathf.Clamp(padding, 0, 8);
            chunkCellSize = Mathf.Clamp(chunkCellSize, 16, 256);

            Vector3 alignedMin = new(
                Mathf.Floor(bounds.min.x / voxelSize) * voxelSize,
                Mathf.Floor(bounds.min.y / voxelSize) * voxelSize,
                Mathf.Floor(bounds.min.z / voxelSize) * voxelSize);
            Vector3 alignedMax = new(
                Mathf.Ceil(bounds.max.x / voxelSize) * voxelSize,
                Mathf.Ceil(bounds.max.y / voxelSize) * voxelSize,
                Mathf.Ceil(bounds.max.z / voxelSize) * voxelSize);
            Vector3 origin = alignedMin - Vector3.one * (padding * voxelSize);
            Vector3 extent = alignedMax - alignedMin;
            Vector3Int size = new(
                Mathf.Max(1, Mathf.RoundToInt(extent.x / voxelSize) + padding * 2),
                Mathf.Max(1, Mathf.RoundToInt(extent.y / voxelSize) + padding * 2),
                Mathf.Max(1, Mathf.RoundToInt(extent.z / voxelSize) + padding * 2));

            var plan = new VoxelGridPlan(voxelSize, origin, size, bounds, chunkCellSize);
            if (plan.CellCount > MaximumDenseCellCount)
                throw new InvalidOperationException(
                    $"La rejilla necesita {plan.CellCount:N0} celdas, por encima del límite seguro de " +
                    $"{MaximumDenseCellCount:N0}. Aumenta la unidad voxel o divide el asset de origen.");
            return plan;
        }
    }
}
