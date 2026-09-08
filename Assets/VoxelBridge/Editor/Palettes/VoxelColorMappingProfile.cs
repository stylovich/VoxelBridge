using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace LocalModels.VoxelBridge
{
    [Serializable]
    public sealed class VoxelColorIdRange
    {
        [SerializeField, Range(VoxelPaletteConstants.MinimumId, VoxelPaletteConstants.MaximumId)]
        private int firstId;
        [SerializeField, Range(VoxelPaletteConstants.MinimumId, VoxelPaletteConstants.MaximumId)]
        private int lastId = VoxelPaletteConstants.MaximumId;

        public int FirstId => firstId;
        public int LastId => lastId;

        public VoxelColorIdRange()
        {
        }

        internal VoxelColorIdRange(int firstId, int lastId)
        {
            this.firstId = firstId;
            this.lastId = lastId;
        }

        internal bool Contains(int id) => id >= firstId && id <= lastId;
    }

    [CreateAssetMenu(fileName = "VoxelColorMappingProfile",
        menuName = "Voxel Bridge/Color Mapping Profile")]
    public sealed class VoxelColorMappingProfile : ScriptableObject
    {
        [SerializeField] private VoxelColorPalette colorPalette;
        [TextArea(2, 5)]
        [SerializeField] private string description =
            "Conjunto de colores globales permitidos durante el mapeo de archivos .vox.";
        [Tooltip("Rangos inclusivos de ColorID permitidos. Los huecos de la paleta global se ignoran.")]
        [SerializeField] private List<VoxelColorIdRange> allowedRanges = new()
        {
            new VoxelColorIdRange(VoxelPaletteConstants.MinimumId, VoxelPaletteConstants.MaximumId)
        };
        [Tooltip("ColorIDs aislados que se añaden a los rangos permitidos.")]
        [SerializeField] private List<int> additionalColorIds = new();
        [Tooltip("A partir de esta distancia OKLab la herramienta muestra una advertencia visual.")]
        [SerializeField, Range(0f, 100f)] private float warningDistance = 8f;
        [Tooltip("Distancia OKLab máxima para asignar un color automáticamente. Los valores más lejanos permanecen sin asignar.")]
        [SerializeField, Range(0f, 100f)] private float maximumAutomaticDistance = 20f;

        public VoxelColorPalette ColorPalette => colorPalette;
        public string Description => description;
        public IReadOnlyList<VoxelColorIdRange> AllowedRanges => allowedRanges;
        public IReadOnlyList<int> AdditionalColorIds => additionalColorIds;
        public float WarningDistance => warningDistance;
        public float MaximumAutomaticDistance => maximumAutomaticDistance;

        internal List<VoxelColorIdRange> MutableAllowedRanges => allowedRanges;
        internal List<int> MutableAdditionalColorIds => additionalColorIds;

        public bool TryGetAllowedColors(out VoxelColorDefinition[] colors, out string error)
        {
            colors = null;
            if (!TryValidate(out error)) return false;

            var explicitIds = new HashSet<int>(additionalColorIds);
            colors = colorPalette.Entries
                .Where(entry => entry != null &&
                    (explicitIds.Contains(entry.Id) || allowedRanges.Any(range => range.Contains(entry.Id))))
                .OrderBy(entry => entry.Id)
                .ToArray();
            if (colors.Length == 0)
            {
                error = "The profile does not allow any active ColorID from the global palette.";
                return false;
            }

            error = null;
            return true;
        }

        public bool TryValidate(out string error)
        {
            if (colorPalette == null)
            {
                error = "The profile has no assigned global color palette.";
                return false;
            }
            if (!colorPalette.TryValidate(out error)) return false;
            if (allowedRanges == null || additionalColorIds == null)
            {
                error = "The profile ColorID collections are not initialized.";
                return false;
            }
            if (float.IsNaN(warningDistance) || float.IsNaN(maximumAutomaticDistance) ||
                warningDistance < 0f || warningDistance > 100f ||
                maximumAutomaticDistance < warningDistance || maximumAutomaticDistance > 100f)
            {
                error = "Thresholds must satisfy 0 ≤ warning ≤ automatic mapping ≤ 100 and be finite.";
                return false;
            }

            for (int index = 0; index < allowedRanges.Count; index++)
            {
                VoxelColorIdRange range = allowedRanges[index];
                if (range == null)
                {
                    error = $"The ColorID range at index {index} is empty.";
                    return false;
                }
                if (range.FirstId < VoxelPaletteConstants.MinimumId ||
                    range.LastId > VoxelPaletteConstants.MaximumId ||
                    range.FirstId > range.LastId)
                {
                    error = $"The range {range.FirstId}..{range.LastId} is invalid.";
                    return false;
                }
            }

            var seenAdditional = new HashSet<int>();
            foreach (int id in additionalColorIds)
            {
                if (id < VoxelPaletteConstants.MinimumId || id > VoxelPaletteConstants.MaximumId)
                {
                    error = $"Additional ColorID {id} is outside the 0..255 range.";
                    return false;
                }
                if (!seenAdditional.Add(id))
                {
                    error = $"Additional ColorID {id} is duplicated.";
                    return false;
                }
                if (!colorPalette.TryGetColor(id, out _))
                {
                    error = $"Additional ColorID {id} does not exist in the global palette.";
                    return false;
                }
            }

            bool hasSelection = colorPalette.Entries.Any(entry => entry != null &&
                (seenAdditional.Contains(entry.Id) || allowedRanges.Any(range => range.Contains(entry.Id))));
            if (!hasSelection)
            {
                error = "The profile does not allow any active ColorID from the global palette.";
                return false;
            }

            error = null;
            return true;
        }

        internal void Initialize(VoxelColorPalette palette)
        {
            colorPalette = palette;
            allowedRanges = new List<VoxelColorIdRange>
            {
                new(VoxelPaletteConstants.MinimumId, VoxelPaletteConstants.MaximumId)
            };
            additionalColorIds = new List<int>();
            warningDistance = 8f;
            maximumAutomaticDistance = 20f;
        }

        internal void ConfigureForTests(VoxelColorPalette palette,
            float warning, float maximum)
        {
            colorPalette = palette;
            warningDistance = warning;
            maximumAutomaticDistance = maximum;
        }

        internal void ConfigureRecommended(VoxelColorPalette palette, string profileDescription,
            IEnumerable<VoxelColorIdRange> ranges, IEnumerable<int> additionalIds,
            float warning = 8f, float maximum = 20f)
        {
            colorPalette = palette;
            description = profileDescription;
            allowedRanges = ranges?.Select(range =>
                    new VoxelColorIdRange(range.FirstId, range.LastId)).ToList() ??
                new List<VoxelColorIdRange>();
            additionalColorIds = additionalIds?.Distinct().OrderBy(id => id).ToList() ??
                new List<int>();
            warningDistance = warning;
            maximumAutomaticDistance = maximum;
        }
    }

    internal readonly struct VoxelColorMatch
    {
        public readonly int ColorId;
        public readonly Color32 Color;
        public readonly float Distance;

        public VoxelColorMatch(int colorId, Color32 color, float distance)
        {
            ColorId = colorId;
            Color = color;
            Distance = distance;
        }
    }

    internal static class VoxelColorPerceptualMatcher
    {
        private const double TieTolerance = 0.000000001;

        public static float Distance(Color32 left, Color32 right) => (float)(Distance(ToOklab(left), ToOklab(right)) * 100.0);

        public static bool TryFindNearest(Color32 source,
            IReadOnlyList<VoxelColorDefinition> candidates, out VoxelColorMatch match)
        {
            match = default;
            if (candidates == null || candidates.Count == 0) return false;

            Oklab sourceLab = ToOklab(source);
            double bestDistance = double.PositiveInfinity;
            int bestId = int.MaxValue;
            Color32 bestColor = default;
            foreach (VoxelColorDefinition candidate in candidates)
            {
                if (candidate == null) continue;
                Color32 candidateColor = candidate.Color;
                double distance = Distance(sourceLab, ToOklab(candidateColor)) * 100.0;
                if (distance + TieTolerance < bestDistance ||
                    Math.Abs(distance - bestDistance) <= TieTolerance && candidate.Id < bestId)
                {
                    bestDistance = distance;
                    bestId = candidate.Id;
                    bestColor = candidateColor;
                }
            }

            if (bestId == int.MaxValue) return false;
            match = new VoxelColorMatch(bestId, bestColor, (float)bestDistance);
            return true;
        }

        private static Oklab ToOklab(Color32 color)
        {
            double r = SrgbToLinear(color.r / 255.0);
            double g = SrgbToLinear(color.g / 255.0);
            double b = SrgbToLinear(color.b / 255.0);

            double l = 0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b;
            double m = 0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b;
            double s = 0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b;

            double lRoot = Math.Pow(Math.Max(0.0, l), 1.0 / 3.0);
            double mRoot = Math.Pow(Math.Max(0.0, m), 1.0 / 3.0);
            double sRoot = Math.Pow(Math.Max(0.0, s), 1.0 / 3.0);
            return new Oklab(
                0.2104542553 * lRoot + 0.7936177850 * mRoot - 0.0040720468 * sRoot,
                1.9779984951 * lRoot - 2.4285922050 * mRoot + 0.4505937099 * sRoot,
                0.0259040371 * lRoot + 0.7827717662 * mRoot - 0.8086757660 * sRoot);
        }

        private static double SrgbToLinear(double value) =>
            value <= 0.04045
                ? value / 12.92
                : Math.Pow((value + 0.055) / 1.055, 2.4);

        private static double Distance(Oklab left, Oklab right)
        {
            double l = left.L - right.L;
            double a = left.A - right.A;
            double b = left.B - right.B;
            return Math.Sqrt(l * l + a * a + b * b);
        }

        private readonly struct Oklab
        {
            public readonly double L;
            public readonly double A;
            public readonly double B;

            public Oklab(double l, double a, double b)
            {
                L = l;
                A = a;
                B = b;
            }
        }
    }
}
