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
        [InspectorName("Opaco estándar")]
        Opaque,
        [InspectorName("Follaje")]
        Foliage,
        [InspectorName("Transparente")]
        Transparent,
        [InspectorName("Especial")]
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

        public int Id => id;
        public string DisplayName => displayName;
        public VoxelSurfaceRenderClass RenderClass => renderClass;
        public float Metallic => metallic;
        public float Smoothness => smoothness;
        public float Emission => emission;
        public float OcclusionMultiplier => occlusionMultiplier;

        internal VoxelSurfaceDefinition(int id, string displayName,
            VoxelSurfaceRenderClass renderClass, float metallic, float smoothness,
            float emission, float occlusionMultiplier)
        {
            this.id = id;
            this.displayName = displayName;
            this.renderClass = renderClass;
            this.metallic = metallic;
            this.smoothness = smoothness;
            this.emission = emission;
            this.occlusionMultiplier = occlusionMultiplier;
        }
    }

    internal static class VoxelPaletteValidation
    {
        public static bool TryValidate(VoxelColorPalette palette, out string error)
        {
            if (palette == null)
            {
                error = "La paleta de colores no está asignada.";
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
                    error = $"El ColorID {entry.Id} contiene componentes fuera del rango 0..1.";
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
                error = "La paleta de superficies no está asignada.";
                return false;
            }

            if (!TryValidateShared(palette.Entries, palette.RetiredIds,
                    entry => entry?.Id ?? -1, entry => entry?.DisplayName,
                    "superficie", out error))
                return false;

            foreach (VoxelSurfaceDefinition entry in palette.Entries)
            {
                if (!Enum.IsDefined(typeof(VoxelSurfaceRenderClass), entry.RenderClass))
                {
                    error = $"El SurfaceID {entry.Id} contiene una clase de render desconocida.";
                    return false;
                }
                if (!IsUnit(entry.Metallic) || !IsUnit(entry.Smoothness) ||
                    !IsUnit(entry.Emission) || !IsUnit(entry.OcclusionMultiplier))
                {
                    error = $"El SurfaceID {entry.Id} contiene propiedades PBR fuera del rango 0..1.";
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
                error = $"La paleta debe contener entre 1 y {VoxelPaletteConstants.EntryCount} entradas de {kind}.";
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
                    error = $"La entrada de {kind} en la posición {i} está vacía.";
                    return false;
                }

                int id = getId(entry);
                if (id < VoxelPaletteConstants.MinimumId || id > VoxelPaletteConstants.MaximumId)
                {
                    error = $"El ID de {kind} {id} está fuera del rango 0..255.";
                    return false;
                }
                if (!activeIds.Add(id))
                {
                    error = $"El ID de {kind} {id} está duplicado.";
                    return false;
                }

                string displayName = getName(entry);
                if (string.IsNullOrWhiteSpace(displayName))
                {
                    error = $"El ID de {kind} {id} no tiene nombre.";
                    return false;
                }
                if (!names.Add(displayName.Trim()))
                {
                    error = $"El nombre de {kind} '{displayName}' está duplicado.";
                    return false;
                }

                hasDefault |= id == VoxelPaletteConstants.DefaultId;
            }

            if (!hasDefault)
            {
                error = $"La paleta requiere una entrada {kind} con ID 0 para los valores predeterminados.";
                return false;
            }

            var seenRetired = new HashSet<int>();
            if (retiredIds != null)
            {
                foreach (int id in retiredIds)
                {
                    if (id <= VoxelPaletteConstants.DefaultId || id > VoxelPaletteConstants.MaximumId)
                    {
                        error = $"El ID retirado {id} está fuera del rango permitido 1..255.";
                        return false;
                    }
                    if (!seenRetired.Add(id))
                    {
                        error = $"El ID retirado {id} aparece más de una vez.";
                        return false;
                    }
                    if (activeIds.Contains(id))
                    {
                        error = $"El ID {id} no puede estar activo y retirado al mismo tiempo.";
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
