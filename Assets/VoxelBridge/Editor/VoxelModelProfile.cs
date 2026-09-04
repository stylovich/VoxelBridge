using System;
using UnityEngine;

namespace LocalModels.VoxelBridge
{
    internal enum VoxelLodTransitionMode
    {
        [InspectorName("Fixed Screen Height")]
        FixedScreenHeight,
        [InspectorName("Adaptive by Model Size")]
        AdaptiveByModelSize
    }

    [CreateAssetMenu(fileName = "VoxelStyleProfile", menuName = "Voxel Bridge/Voxel Style Profile")]
    public sealed class VoxelStyleProfile : ScriptableObject
    {
        [Tooltip("Tamaño de un vóxel de LOD0 en unidades de Unity. El valor predeterminado 0.032 equivale a 3.2 cm.")]
        [SerializeField, Min(0.001f)] private float baseVoxelSize = 0.032f;
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
        [Tooltip("Fixed Screen Height utiliza los porcentajes de pantalla configurados. Adaptive by Model Size ajusta toda la curva según el tamaño final del modelo.")]
        [InspectorName("Transition Mode")]
        [SerializeField] private VoxelLodTransitionMode lodTransitionMode =
            VoxelLodTransitionMode.FixedScreenHeight;
        [Tooltip("Tamaño en metros del modelo para el que se ajustaron las transiciones base. Un vehículo grande suele estar cerca de 4 m.")]
        [InspectorName("Reference Model Size")]
        [SerializeField, Min(0.01f)] private float lodReferenceModelSize = 4f;
        [Tooltip("Intensidad del ajuste por tamaño. 0 conserva los porcentajes base, 0.5 ofrece una compensación equilibrada y 1 aproxima distancias de transición constantes.")]
        [InspectorName("Size Adaptation Strength")]
        [SerializeField, Range(0f, 1f)] private float lodSizeAdaptationStrength = 0.5f;
        [Tooltip("Límite inferior del factor aplicado a modelos pequeños. 0.35 evita que las transiciones se alejen excesivamente.")]
        [InspectorName("Minimum Transition Scale")]
        [SerializeField, Min(0.01f)] private float lodMinimumTransitionScale = 0.35f;
        [Tooltip("Límite superior de la curva adaptativa general. 2 adelanta las transiciones de modelos grandes sin concentrarlas demasiado cerca de la cámara; el refuerzo posterior dispone de su propio máximo.")]
        [InspectorName("Maximum Transition Scale")]
        [SerializeField, Min(0.01f)] private float lodMaximumTransitionScale = 2f;
        [Tooltip("Tamaño a partir del cual se refuerza gradualmente la adaptación de los LOD. Los modelos iguales o menores conservan la curva adaptativa normal. 6 m es un punto de partida para secciones grandes de edificios.")]
        [InspectorName("Large Model Threshold")]
        [SerializeField, Min(0.01f)] private float lodLargeModelSizeThreshold = 6f;
        [Tooltip("Exponente adicional aplicado solamente por encima del umbral de modelo grande. 0 desactiva el refuerzo; 0.25 adelanta moderadamente los LOD de menor resolución sin producir un salto en el umbral.")]
        [InspectorName("Large Model Additional Strength")]
        [SerializeField, Range(0f, 1f)] private float lodLargeModelAdditionalStrength = 0.25f;
        [Tooltip("Límite final del factor después del refuerzo para modelos grandes. Con una curva 0.30 / 0.18 / 0.10, un valor de 2.5 limita las transiciones voxel a 0.75 / 0.45 / 0.25. Al añadir un impostor, la distribución de estructuras grandes converge gradualmente a 0.75 / 0.55 / 0.35 para evitar que los LOD intermedios abarquen distancias excesivas.")]
        [InspectorName("Large Model Maximum Scale")]
        [SerializeField, Min(0.01f)] private float lodLargeModelMaximumTransitionScale = 2.5f;

        [Header("Shadow Optimization")]
        [Tooltip("Desactiva la proyección de sombras en los LOD más lejanos de modelos pequeños. El tamaño se evalúa en el espacio local del prefab, antes de aplicar la escala de cada instancia.")]
        [InspectorName("Reduce Shadows by Size")]
        [SerializeField] private bool reduceSmallObjectShadows = true;
        [Tooltip("Los modelos menores que este tamaño dejan de proyectar sombras en su último LOD voxel y en el impostor. 1 m es un valor equilibrado para props pequeños.")]
        [InspectorName("Last LOD Shadow Threshold")]
        [SerializeField, Min(0.01f)] private float lastLodShadowSizeThreshold = 1f;
        [Tooltip("Los modelos menores que este tamaño dejan de proyectar sombras desde el penúltimo LOD voxel. Los niveles posteriores y el impostor también quedan sin sombras. 0,5 m es adecuado para decoración pequeña.")]
        [InspectorName("Penultimate LOD Shadow Threshold")]
        [SerializeField, Min(0.01f)] private float penultimateLodShadowSizeThreshold = 0.5f;

        [Header("Grid Alignment")]
        [Tooltip("Al colocar automáticamente el resultado en una escena, ajusta la posición mundial de su pivote al múltiplo más cercano de la unidad voxel base. El desplazamiento máximo es media celda por eje y la geometría conserva su alineación local.")]
        [InspectorName("Snap Pivots on Placement")]
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
            if (lodScreenHeights == null || index < 0 || index >= LodCount ||
                index >= lodScreenHeights.Length)
                throw new ArgumentOutOfRangeException(nameof(index));
            return lodScreenHeights[index];
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
            float transitionScale = Mathf.Clamp(rawScale, lodMinimumTransitionScale,
                lodMaximumTransitionScale);
            float largeModelThreshold = Mathf.Max(0.01f, lodLargeModelSizeThreshold);
            float additionalStrength = Mathf.Clamp01(lodLargeModelAdditionalStrength);
            if (modelSize <= largeModelThreshold || additionalStrength <= 0f)
                return transitionScale;

            float largeModelScale = Mathf.Pow(
                modelSize / largeModelThreshold, additionalStrength);
            float largeModelMaximum = Mathf.Max(
                lodMaximumTransitionScale, lodLargeModelMaximumTransitionScale);
            return Mathf.Min(transitionScale * largeModelScale, largeModelMaximum);
        }

        public float GetLargeModelLodRangeBlend(float modelSize)
        {
            if (!UsesAdaptiveLodTransitions || !IsFinitePositive(modelSize)) return 0f;

            float generalMaximum = Mathf.Max(0.01f, lodMaximumTransitionScale);
            float largeMaximum = Mathf.Max(generalMaximum, lodLargeModelMaximumTransitionScale);
            if (Mathf.Approximately(generalMaximum, largeMaximum)) return 0f;

            return Mathf.InverseLerp(
                generalMaximum, largeMaximum, GetLodTransitionScale(modelSize));
        }

        public bool TryValidate(out string error)
        {
            if (baseVoxelSize <= 0f || float.IsNaN(baseVoxelSize) || float.IsInfinity(baseVoxelSize))
            {
                error = "The base voxel size must be finite and greater than zero.";
                return false;
            }
            if (lodMultipliers == null || lodMultipliers.Length == 0 || lodMultipliers.Length > 8)
            {
                error = "The profile must contain between 1 and 8 LOD levels.";
                return false;
            }
            if (lodMultipliers[0] != 1)
            {
                error = "LOD0 must use multiplier 1 to represent the base voxel size.";
                return false;
            }
            if (lodScreenHeights == null || lodScreenHeights.Length < lodMultipliers.Length)
            {
                error = "Each LOD level requires an explicit screen-height transition.";
                return false;
            }
            if (!Enum.IsDefined(typeof(VoxelLodTransitionMode), lodTransitionMode))
            {
                error = "The LOD transition mode is unsupported.";
                return false;
            }
            if (UsesAdaptiveLodTransitions &&
                (!IsFinitePositive(lodReferenceModelSize) ||
                 !IsFinitePositive(lodMinimumTransitionScale) ||
                 !IsFinitePositive(lodMaximumTransitionScale) ||
                 lodMinimumTransitionScale > lodMaximumTransitionScale ||
                 float.IsNaN(lodSizeAdaptationStrength) ||
                 float.IsInfinity(lodSizeAdaptationStrength) ||
                 lodSizeAdaptationStrength < 0f || lodSizeAdaptationStrength > 1f ||
                 !IsFinitePositive(lodLargeModelSizeThreshold) ||
                 !IsFinitePositive(lodLargeModelMaximumTransitionScale) ||
                 lodLargeModelMaximumTransitionScale < lodMaximumTransitionScale ||
                 float.IsNaN(lodLargeModelAdditionalStrength) ||
                 float.IsInfinity(lodLargeModelAdditionalStrength) ||
                 lodLargeModelAdditionalStrength < 0f ||
                 lodLargeModelAdditionalStrength > 1f))
            {
                error = "LOD adaptation requires strengths between 0 and 1, positive sizes and ordered maximum scales. The large-model maximum cannot be lower than the general maximum.";
                return false;
            }
            if (reduceSmallObjectShadows &&
                (!IsFinitePositive(lastLodShadowSizeThreshold) ||
                 !IsFinitePositive(penultimateLodShadowSizeThreshold) ||
                 penultimateLodShadowSizeThreshold > lastLodShadowSizeThreshold))
            {
                error = "Shadow optimization requires positive thresholds. The penultimate LOD threshold cannot exceed the final LOD threshold.";
                return false;
            }

            int previous = 0;
            float previousHeight = 1.01f;
            for (int i = 0; i < lodMultipliers.Length; i++)
            {
                int multiplier = lodMultipliers[i];
                if (multiplier < 1 || (multiplier & (multiplier - 1)) != 0 || multiplier <= previous)
                {
                    error = "LOD multipliers must be strictly increasing powers of two.";
                    return false;
                }
                previous = multiplier;

                float height = GetLodScreenHeight(i);
                if (!float.IsFinite(height) || height <= 0f || height > 1f || height >= previousHeight)
                {
                    error = "LOD transitions must be finite, greater than zero, at most 1, and strictly decreasing.";
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
                    $"The grid requires {plan.CellCount:N0} cells, exceeding the safe limit of " +
                    $"{MaximumDenseCellCount:N0}. Increase the voxel size or split the source asset.");
            return plan;
        }
    }
}
