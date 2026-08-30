using System;
using UnityEngine;

namespace LocalModels.VoxelBridge
{
    internal enum VoxelLodTransitionMode
    {
        [InspectorName("Fija por pantalla")]
        FixedScreenHeight,
        [InspectorName("Adaptativa por tamaño")]
        AdaptiveByModelSize
    }

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
        [Tooltip("Fijo usa los porcentajes de pantalla sin modificarlos. Adaptativo ajusta toda la curva según el tamaño final del modelo.")]
        [InspectorName("Modo de transición")]
        [SerializeField] private VoxelLodTransitionMode lodTransitionMode =
            VoxelLodTransitionMode.FixedScreenHeight;
        [Tooltip("Tamaño en metros del modelo para el que se ajustaron las transiciones base. Un vehículo grande suele estar cerca de 4 m.")]
        [InspectorName("Tamaño de referencia")]
        [SerializeField, Min(0.01f)] private float lodReferenceModelSize = 4f;
        [Tooltip("Intensidad del ajuste por tamaño. 0 conserva los porcentajes base, 0.5 ofrece una compensación equilibrada y 1 aproxima distancias de transición constantes.")]
        [InspectorName("Intensidad de adaptación")]
        [SerializeField, Range(0f, 1f)] private float lodSizeAdaptationStrength = 0.5f;
        [Tooltip("Límite inferior del factor aplicado a modelos pequeños. 0.35 evita que las transiciones se alejen excesivamente.")]
        [InspectorName("Factor mínimo")]
        [SerializeField, Min(0.01f)] private float lodMinimumTransitionScale = 0.35f;
        [Tooltip("Límite superior del factor aplicado a modelos grandes. 2 permite adelantar las transiciones sin concentrarlas demasiado cerca de la cámara.")]
        [InspectorName("Factor máximo")]
        [SerializeField, Min(0.01f)] private float lodMaximumTransitionScale = 2f;

        [Header("Optimización de sombras")]
        [Tooltip("Desactiva la proyección de sombras en los LOD más lejanos de modelos pequeños. El tamaño se evalúa en el espacio local del prefab, antes de aplicar la escala de cada instancia.")]
        [InspectorName("Reducir sombras por tamaño")]
        [SerializeField] private bool reduceSmallObjectShadows = true;
        [Tooltip("Los modelos menores que este tamaño dejan de proyectar sombras en su último LOD voxel y en el impostor. 1 m es un valor equilibrado para props pequeños.")]
        [InspectorName("Umbral para último LOD")]
        [SerializeField, Min(0.01f)] private float lastLodShadowSizeThreshold = 1f;
        [Tooltip("Los modelos menores que este tamaño dejan de proyectar sombras desde el penúltimo LOD voxel. Los niveles posteriores y el impostor también quedan sin sombras. 0,5 m es adecuado para decoración pequeña.")]
        [InspectorName("Umbral para penúltimo LOD")]
        [SerializeField, Min(0.01f)] private float penultimateLodShadowSizeThreshold = 0.5f;

        [Header("Alineación al grid")]
        [Tooltip("Al colocar automáticamente el resultado en una escena, ajusta la posición mundial de su pivote al múltiplo más cercano de la unidad voxel base. El desplazamiento máximo es media celda por eje y la geometría conserva su alineación local.")]
        [InspectorName("Alinear pivotes al colocar")]
        [SerializeField] private bool snapPlacedPivotsToVoxelGrid = true;

        public float BaseVoxelSize => Mathf.Max(0.001f, baseVoxelSize);
        public int ChunkCellSize => Mathf.Clamp(chunkCellSize, 16, 256);
        public int Padding => Mathf.Clamp(padding, 0, 8);
        public bool FillInterior => fillInterior;
        public bool HideInternalCavities => hideInternalCavities;
        public int LodCount => lodMultipliers?.Length ?? 0;
        public bool UsesAdaptiveLodTransitions =>
            lodTransitionMode == VoxelLodTransitionMode.AdaptiveByModelSize;

        public int GetFirstShadowlessVoxelLodIndex(float modelSize, int voxelLodCount)
        {
            if (!reduceSmallObjectShadows || voxelLodCount <= 0 || !IsFinitePositive(modelSize))
                return -1;
            if (modelSize < penultimateLodShadowSizeThreshold)
                return Mathf.Max(0, voxelLodCount - 2);
            return modelSize < lastLodShadowSizeThreshold ? voxelLodCount - 1 : -1;
        }

        public Vector3 GetSnappedWorldPosition(Vector3 worldPosition)
        {
            if (!snapPlacedPivotsToVoxelGrid) return worldPosition;
            float gridSize = BaseVoxelSize;
            return new Vector3(
                Mathf.Round(worldPosition.x / gridSize) * gridSize,
                Mathf.Round(worldPosition.y / gridSize) * gridSize,
                Mathf.Round(worldPosition.z / gridSize) * gridSize);
        }

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

        public float GetLodScreenHeight(int index, float modelSize)
        {
            return GetLodScreenHeightForScale(index, GetLodTransitionScale(modelSize));
        }

        public float GetMinimumLodScreenHeight(int index)
        {
            float scale = UsesAdaptiveLodTransitions ? lodMinimumTransitionScale : 1f;
            return GetLodScreenHeightForScale(index, scale);
        }

        private float GetLodScreenHeightForScale(int index, float scale)
        {
            float firstHeight = GetLodScreenHeight(0);
            if (firstHeight > 0f)
                scale = Mathf.Min(scale, 0.99f / firstHeight);
            return Mathf.Clamp(GetLodScreenHeight(index) * scale, 0.0001f, 0.99f);
        }

        public float GetLodTransitionScale(float modelSize)
        {
            if (!UsesAdaptiveLodTransitions || !IsFinitePositive(modelSize)) return 1f;
            float referenceSize = Mathf.Max(0.01f, lodReferenceModelSize);
            float rawScale = Mathf.Pow(modelSize / referenceSize,
                Mathf.Clamp01(lodSizeAdaptationStrength));
            return Mathf.Clamp(rawScale, lodMinimumTransitionScale,
                lodMaximumTransitionScale);
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
            if (UsesAdaptiveLodTransitions &&
                (!IsFinitePositive(lodReferenceModelSize) ||
                 !IsFinitePositive(lodMinimumTransitionScale) ||
                 !IsFinitePositive(lodMaximumTransitionScale) ||
                 lodMinimumTransitionScale > lodMaximumTransitionScale ||
                 float.IsNaN(lodSizeAdaptationStrength) ||
                 float.IsInfinity(lodSizeAdaptationStrength) ||
                 lodSizeAdaptationStrength < 0f || lodSizeAdaptationStrength > 1f))
            {
                error = "La adaptación LOD requiere una intensidad entre 0 y 1, un tamaño de referencia y límites positivos, con el mínimo menor o igual que el máximo.";
                return false;
            }
            if (reduceSmallObjectShadows &&
                (!IsFinitePositive(lastLodShadowSizeThreshold) ||
                 !IsFinitePositive(penultimateLodShadowSizeThreshold) ||
                 penultimateLodShadowSizeThreshold > lastLodShadowSizeThreshold))
            {
                error = "La optimización de sombras requiere umbrales positivos y el umbral del penúltimo LOD no puede superar al del último LOD.";
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

        private static bool IsFinitePositive(float value) =>
            value > 0f && !float.IsNaN(value) && !float.IsInfinity(value);
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
