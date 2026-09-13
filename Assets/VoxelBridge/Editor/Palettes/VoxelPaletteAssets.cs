using System;
using System.Collections.Generic;
using UnityEngine;

namespace LocalModels.VoxelBridge
{
    internal static class VoxelPaletteConstants
    {
        public const int EntryCount = 256;
        public const int MinimumId = 0;
        public const int MaximumId = EntryCount - 1;
        public const int DefaultId = 0;
    }

    public enum VoxelSurfaceRenderClass
    {
        [InspectorName("Standard Opaque")]
        Opaque,
        Foliage,
        [InspectorName("Glass (Transparent)")]
        Transparent,
        Special
    }

    [Serializable]
    public sealed class VoxelColorDefinition
    {
        [SerializeField] private int id;
        [SerializeField] private string displayName;
        [ColorUsage(false, false)]
        [SerializeField] private Color color;

        public int Id => id;
        public string DisplayName => displayName;
        public Color Color => color;

        internal VoxelColorDefinition(int id, string displayName, Color color)
        {
            this.id = id;
            this.displayName = displayName;
            this.color = color;
        }
    }

    [Serializable]
    public sealed class VoxelSurfaceDefinition
    {
        [SerializeField] private int id;
        [SerializeField] private string displayName;
        [SerializeField] private VoxelSurfaceRenderClass renderClass;
        [Range(0f, 1f)]
        [SerializeField] private float metallic;
        [Range(0f, 1f)]
        [SerializeField] private float smoothness;
        [Range(0f, 1f)]
        [SerializeField] private float emission;
        [Range(0f, 1f)]
        [SerializeField] private float occlusionMultiplier;
        [Range(0f, 1f)]
        [SerializeField] private float opacity = .25f;
        [Range(0f, .25f)]
        [SerializeField] private float cellHeightVariation;

        public int Id => id;
        public string DisplayName => displayName;
        public VoxelSurfaceRenderClass RenderClass => renderClass;
        public float Metallic => metallic;
        public float Smoothness => smoothness;
        public float Emission => emission;
        public float OcclusionMultiplier => occlusionMultiplier;
        public float Opacity => opacity;
        public float CellHeightVariation => cellHeightVariation;
        public bool SupportsVoxelRendering => renderClass == VoxelSurfaceRenderClass.Opaque || renderClass == VoxelSurfaceRenderClass.Transparent;

        internal VoxelSurfaceDefinition(int id, string displayName,
            VoxelSurfaceRenderClass renderClass, float metallic, float smoothness,
            float emission, float occlusionMultiplier, float opacity = .25f, float cellHeightVariation = 0f)
        {
            this.id = id;
            this.displayName = displayName;
            this.renderClass = renderClass;
            this.metallic = metallic;
            this.smoothness = smoothness;
            this.emission = emission;
            this.occlusionMultiplier = occlusionMultiplier;
            this.opacity = opacity;
            this.cellHeightVariation = cellHeightVariation;
        }
    }

    internal static class VoxelPaletteValidation
    {
        public static bool TryValidate(VoxelColorPalette palette, out string error)
        {
            if (palette == null)
            {
                error = "The color palette is not assigned.";
                return false;
            }

            if (!TryValidateShared(palette.Entries, palette.RetiredIds,
                    entry => entry?.Id ?? -1, entry => entry?.DisplayName,
                    "color", out error))
                return false;

            foreach (VoxelColorDefinition entry in palette.Entries)
            {
                Color color = entry.Color;
                if (!IsUnit(color.r) || !IsUnit(color.g) ||
                    !IsUnit(color.b) || !IsUnit(color.a))
                {
                    error = $"ColorID {entry.Id} contains components outside the 0..1 range.";
                    return false;
                }
            }

            error = null;
            return true;
        }

        public static bool TryValidate(VoxelSurfacePalette palette, out string error)
        {
            if (palette == null)
            {
                error = "The surface palette is not assigned.";
                return false;
            }

            if (!TryValidateShared(palette.Entries, palette.RetiredIds,
                    entry => entry?.Id ?? -1, entry => entry?.DisplayName,
                    "surface", out error))
                return false;

            foreach (VoxelSurfaceDefinition entry in palette.Entries)
            {
                if (!Enum.IsDefined(typeof(VoxelSurfaceRenderClass), entry.RenderClass))
                {
                    error = $"SurfaceID {entry.Id} has an unknown render class.";
                    return false;
                }
                if (!IsUnit(entry.Metallic) || !IsUnit(entry.Smoothness) ||
                    !IsUnit(entry.Emission) || !IsUnit(entry.OcclusionMultiplier) || !IsUnit(entry.Opacity))
                {
                    error = $"SurfaceID {entry.Id} contains PBR properties outside the 0..1 range.";
                    return false;
                }
                if (!IsUnit(entry.CellHeightVariation) || entry.CellHeightVariation > .25f)
                {
                    error = $"SurfaceID {entry.Id} has Cell Height Variation outside the finite 0..0.25 range.";
                    return false;
                }
            }

            error = null;
            return true;
        }

        private static bool TryValidateShared<T>(IReadOnlyList<T> entries,
            IReadOnlyList<int> retiredIds, Func<T, int> getId,
            Func<T, string> getName, string kind, out string error)
            where T : class
        {
            if (entries == null || entries.Count == 0 || entries.Count > VoxelPaletteConstants.EntryCount)
            {
                error = $"The palette must contain between 1 and {VoxelPaletteConstants.EntryCount} {kind} entries.";
                return false;
            }

            var activeIds = new HashSet<int>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool hasDefault = false;
            for (int i = 0; i < entries.Count; i++)
            {
                T entry = entries[i];
                if (entry == null)
                {
                    error = $"The {kind} entry at index {i} is empty.";
                    return false;
                }

                int id = getId(entry);
                if (id < VoxelPaletteConstants.MinimumId || id > VoxelPaletteConstants.MaximumId)
                {
                    error = $"The {kind} ID {id} is outside the 0..255 range.";
                    return false;
                }
                if (!activeIds.Add(id))
                {
                    error = $"The {kind} ID {id} is duplicated.";
                    return false;
                }

                string displayName = getName(entry);
                if (string.IsNullOrWhiteSpace(displayName))
                {
                    error = $"The {kind} ID {id} has no name.";
                    return false;
                }
                if (!names.Add(displayName.Trim()))
                {
                    error = $"The {kind} name '{displayName}' is duplicated.";
                    return false;
                }

                hasDefault |= id == VoxelPaletteConstants.DefaultId;
            }

            if (!hasDefault)
            {
                error = $"The palette requires a default {kind} entry with ID 0.";
                return false;
            }

            var seenRetired = new HashSet<int>();
            if (retiredIds != null)
            {
                foreach (int id in retiredIds)
                {
                    if (id <= VoxelPaletteConstants.DefaultId || id > VoxelPaletteConstants.MaximumId)
                    {
                        error = $"Retired ID {id} is outside the allowed 1..255 range.";
                        return false;
                    }
                    if (!seenRetired.Add(id))
                    {
                        error = $"Retired ID {id} appears more than once.";
                        return false;
                    }
                    if (activeIds.Contains(id))
                    {
                        error = $"ID {id} cannot be both active and retired.";
                        return false;
                    }
                }
            }

            error = null;
            return true;
        }

        private static bool IsUnit(float value) =>
            value >= 0f && value <= 1f && !float.IsNaN(value) && !float.IsInfinity(value);
    }
}
