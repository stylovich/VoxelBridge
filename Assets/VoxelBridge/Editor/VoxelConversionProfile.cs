using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace LocalModels.VoxelBridge
{
    [Serializable]
    public sealed class VoxelMaterialConversionRule
    {
        public Material material;
        public VoxelConversionAction action;
        [Tooltip("SurfaceID explícito para este material. -1 permite detectar emisión o utilizar la superficie predeterminada.")]
        [Range(-1, 255)] public int surfaceId = -1;
    }

    [CreateAssetMenu(fileName = "VoxelConversionProfile", menuName = "Voxel Bridge/Conversion Profile")]
    public sealed class VoxelConversionProfile : ScriptableObject
    {
        [Tooltip("Perfil de colores globales. Es obligatorio para conservar SurfaceID durante la conversión.")]
        public VoxelColorMappingProfile colorMapping;
        public VoxelSurfacePalette surfacePalette;
        [Range(0, 255)] public int defaultSurfaceId;
        public List<VoxelMaterialConversionRule> materialRules = new();
        [Tooltip("Reconoce emisión de HDRP/Lit y Standard con UV0. Las asignaciones explícitas de SurfaceID tienen prioridad.")]
        public bool detectEmission = true;
        [Range(0, 255)] public int emissiveSurfaceId = 13;
        [Tooltip("Umbral de emisión lineal, antes de normalizar el color HDR. No representa la intensidad final del shader de producción.")]
        [Min(0.0001f)] public float emissionThreshold = 0.01f;

        internal void Validate()
        {
            string error = null;
            if (colorMapping == null || !colorMapping.TryGetAllowedColors(out _, out error))
                throw new InvalidDataException(colorMapping == null ? "Assign a color mapping profile." : error);
            if (surfacePalette == null || !surfacePalette.TryValidate(out error))
                throw new InvalidDataException(surfacePalette == null ? "Assign a surface palette." : error);
            ValidateSurface(defaultSurfaceId);
            if (detectEmission)
            {
                ValidateSurface(emissiveSurfaceId);
                surfacePalette.TryGetSurface(emissiveSurfaceId, out var surface);
                if (surface.Emission <= 0) throw new InvalidDataException("The emission surface must have nonzero emission.");
                if (!float.IsFinite(emissionThreshold) || emissionThreshold <= 0)
                    throw new InvalidDataException("The emission threshold must be positive and finite.");
            }
            if (materialRules == null) throw new InvalidDataException("The material rule list is missing.");
            var seen = new HashSet<Material>();
            foreach (var rule in materialRules)
            {
                if (rule == null || rule.material == null) throw new InvalidDataException("A material rule has no material.");
                if (!seen.Add(rule.material)) throw new InvalidDataException($"Material '{rule.material.name}' has duplicate rules.");
                ValidateAction(rule.action);
                if (rule.surfaceId < -1 || rule.surfaceId > 255) throw new InvalidDataException("SurfaceID must be -1 or 0..255.");
                if (rule.action == VoxelConversionAction.Voxelize && rule.surfaceId >= 0) ValidateSurface(rule.surfaceId);
            }
        }

        internal void ValidateSurface(int id)
        {
            if (id < 0 || id > 255 || surfacePalette == null || !surfacePalette.TryGetSurface(id, out var surface))
                throw new InvalidDataException($"Unknown SurfaceID {id} in the conversion profile.");
            if (surface.RenderClass != VoxelSurfaceRenderClass.Opaque)
                throw new InvalidDataException($"SurfaceID {id} is not opaque. Use Keep Original for transparent geometry.");
        }

        internal void ValidateForExport()
        {
            Validate();
            if (!AssetDatabase.Contains(this) || !AssetDatabase.Contains(colorMapping) ||
                !AssetDatabase.Contains(colorMapping.ColorPalette) || !AssetDatabase.Contains(surfacePalette))
                throw new InvalidDataException("Save the conversion profile, color mapping profile and global palettes as assets before exporting.");
        }

        internal static void ValidateAction(VoxelConversionAction action)
        {
            if (!Enum.IsDefined(typeof(VoxelConversionAction), action)) throw new InvalidDataException("Unknown conversion action.");
        }

        internal static string Fingerprint(VoxelConversionProfile profile)
        {
            if (profile == null) return "conversion:none";
            return Hash128.Compute(EditorJsonUtility.ToJson(profile) + ":" +
                Dependency(profile) + ":" + Dependency(profile.colorMapping) + ":" +
                Dependency(profile.colorMapping?.ColorPalette) + ":" + Dependency(profile.surfacePalette)).ToString();
        }

        private static string Dependency(UnityEngine.Object value) => value == null ? "null" :
            EditorJsonUtility.ToJson(value) + ":" + (AssetDatabase.Contains(value)
                ? AssetDatabase.GetAssetDependencyHash(AssetDatabase.GetAssetPath(value)).ToString() : value.GetInstanceID().ToString());

        internal static string RuleFingerprint(UnityEngine.Object source)
        {
            if (!(source is GameObject root)) return "none";
            return Hash128.Compute(string.Join("|", root.GetComponentsInChildren<VoxelConversionRule>(true)
                .Select(rule => EditorJsonUtility.ToJson(rule)))).ToString();
        }
    }

    internal readonly struct VoxelResolvedRule
    {
        public readonly VoxelConversionAction Action;
        public readonly int SurfaceId;
        public VoxelResolvedRule(VoxelConversionAction action, int surfaceId) { Action = action; SurfaceId = surfaceId; }

        internal static VoxelResolvedRule Resolve(Transform root, Transform current, Material material, VoxelConversionProfile profile)
        {
            var materialRule = profile?.materialRules?.FirstOrDefault(r => r != null && r.material == material);
            var action = materialRule?.action ?? VoxelConversionAction.Voxelize;
            int surfaceId = materialRule?.surfaceId ?? -1;
            for (Transform t = current; t != null; t = t.parent)
            {
                var marker = t.GetComponent<VoxelConversionRule>();
                if (marker != null && marker.enabled && (t == current || marker.ApplyToChildren))
                {
                    action = marker.Action;
                    if (marker.SurfaceId >= 0) surfaceId = marker.SurfaceId;
                    if (marker.SurfaceId < -1 || marker.SurfaceId > 255) throw new InvalidDataException("Invalid marker SurfaceID.");
                    break;
                }
                if (t == root) break;
            }
            VoxelConversionProfile.ValidateAction(action);
            if (action == VoxelConversionAction.Voxelize && surfaceId >= 0)
            {
                if (profile == null) throw new InvalidDataException("An explicit SurfaceID requires a conversion profile.");
                profile.ValidateSurface(surfaceId);
            }
            return new VoxelResolvedRule(action, surfaceId);
        }
    }

    internal sealed class VoxelConversionColorMapper
    {
        private readonly VoxelConversionProfile profile;
        private readonly VoxelColorDefinition[] candidates;
        private readonly Dictionary<int, int> cache = new();
        internal float MaximumDistance { get; private set; }
        internal VoxelConversionColorMapper(VoxelConversionProfile profile)
        {
            this.profile = profile;
            profile.Validate();
            profile.colorMapping.TryGetAllowedColors(out candidates, out _);
        }
        internal ushort Map(Color32 color, int surfaceId)
        {
            int key = color.r | color.g << 8 | color.b << 16;
            if (!cache.TryGetValue(key, out int id))
            {
                if (!VoxelColorPerceptualMatcher.TryFindNearest(color, candidates, out var match) ||
                    match.Distance > profile.colorMapping.MaximumAutomaticDistance)
                    throw new InvalidDataException($"Color #{color.r:X2}{color.g:X2}{color.b:X2} exceeds the color mapping threshold. Adjust the mapping profile.");
                id = match.ColorId;
                MaximumDistance = Mathf.Max(MaximumDistance, match.Distance);
                if (cache.Count >= 65536) cache.Clear();
                cache.Add(key, id);
            }
            return VoxelSemanticEncoding.Pack(id, surfaceId < 0 ? profile.defaultSurfaceId : surfaceId);
        }
    }
}
