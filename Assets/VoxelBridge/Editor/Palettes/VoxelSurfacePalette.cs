using System;
using System.Collections.Generic;
using UnityEngine;

namespace LocalModels.VoxelBridge
{
    [CreateAssetMenu(fileName = "VoxelSurfacePalette",
        menuName = "Voxel Bridge/Global Surface Palette")]
    public sealed class VoxelSurfacePalette : ScriptableObject
    {
        [SerializeField] private List<VoxelSurfaceDefinition> entries = CreateRecommendedEntries();
        [SerializeField, HideInInspector] private List<int> retiredIds = new();
        [SerializeField, HideInInspector] private Texture2D generatedLut;
        [SerializeField, HideInInspector] private string generatedContentHash;

        public IReadOnlyList<VoxelSurfaceDefinition> Entries => entries;
        public Texture2D GeneratedLut => generatedLut;
        public string GeneratedContentHash => generatedContentHash;

        internal IReadOnlyList<int> RetiredIds => retiredIds;
        internal List<VoxelSurfaceDefinition> MutableEntries => entries;

        public bool TryGetSurface(int id, out VoxelSurfaceDefinition surface)
        {
            surface = null;
            bool found = false;
            if (entries == null) return false;

            foreach (VoxelSurfaceDefinition entry in entries)
            {
                if (entry == null || entry.Id != id) continue;
                if (found) return false;
                surface = entry;
                found = true;
            }

            return found;
        }

        public bool TryValidate(out string error) =>
            VoxelPaletteValidation.TryValidate(this, out error);

        // Explicit opt-in only: existing assignments and retired IDs keep their meaning.
        // The caller owns Undo, asset persistence, and rebuilding the generated LUT.
        internal bool TryAppendRecommendedEntries(out int addedCount, out string error)
        {
            addedCount = 0;
            if (!TryValidate(out error)) return false;

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (VoxelSurfaceDefinition entry in entries)
                names.Add(entry.DisplayName.Trim());

            var additions = new List<VoxelSurfaceDefinition>();
            foreach (VoxelSurfaceDefinition recommended in CreateRecommendedEntries())
            {
                if (ContainsId(recommended.Id) ||
                    (retiredIds != null && retiredIds.Contains(recommended.Id)) ||
                    !names.Add(recommended.DisplayName.Trim())) continue;
                additions.Add(recommended);
            }

            if (additions.Count > 0)
            {
                entries.AddRange(additions);
                generatedContentHash = null;
            }
            addedCount = additions.Count;
            error = null;
            return true;
        }

        internal bool TryAddEntry(out int addedId, out string error)
        {
            EnsureCollections();
            for (int id = 1; id <= VoxelPaletteConstants.MaximumId; id++)
            {
                if (ContainsId(id) || retiredIds.Contains(id)) continue;
                entries.Add(new VoxelSurfaceDefinition(
                    id, $"Surface {id}", VoxelSurfaceRenderClass.Opaque,
                    0f, 0.35f, 0f, 1f));
                generatedContentHash = null;
                addedId = id;
                error = null;
                return true;
            }

            addedId = -1;
            error = "No surface IDs are available. Retired IDs are not automatically reused.";
            return false;
        }

        internal bool TryRemoveEntryAt(int index, out string error)
        {
            EnsureCollections();
            if (index < 0 || index >= entries.Count || entries[index] == null)
            {
                error = "The selected entry is invalid.";
                return false;
            }

            int id = entries[index].Id;
            if (id == VoxelPaletteConstants.DefaultId)
            {
                error = "SurfaceID 0 is the default entry and cannot be removed.";
                return false;
            }

            entries.RemoveAt(index);
            if (id >= VoxelPaletteConstants.MinimumId &&
                id <= VoxelPaletteConstants.MaximumId && !retiredIds.Contains(id))
                retiredIds.Add(id);
            retiredIds.Sort();
            generatedContentHash = null;
            error = null;
            return true;
        }

        internal void ResetRecommendedProfiles()
        {
            entries = CreateRecommendedEntries();
            retiredIds = new List<int>();
            generatedContentHash = null;
        }

        internal void SetGeneratedLut(Texture2D texture, string contentHash)
        {
            generatedLut = texture;
            generatedContentHash = contentHash;
        }

        private bool ContainsId(int id)
        {
            foreach (VoxelSurfaceDefinition entry in entries)
            {
                if (entry != null && entry.Id == id) return true;
            }
            return false;
        }

        private void EnsureCollections()
        {
            entries ??= new List<VoxelSurfaceDefinition>();
            retiredIds ??= new List<int>();
        }

        private static List<VoxelSurfaceDefinition> CreateRecommendedEntries() => new()
        {
            Surface(0, "Default", 0f, 0f, 0f, 1f),
            Surface(1, "Concrete", 0f, 0.15f, 0f, 0.9f),
            Surface(2, "Fabric", 0f, 0.10f, 0f, 0.95f),
            Surface(3, "Plastic Matte", 0f, 0.28f, 0f, 1f),
            Surface(4, "Plastic Glossy", 0f, 0.78f, 0f, 1f),
            Surface(5, "Painted Metal", 0f, 0.58f, 0f, 1f),
            Surface(6, "Steel", 1f, 0.66f, 0f, 1f),
            Surface(7, "Aluminum", 1f, 0.46f, 0f, 1f),
            Surface(8, "Rubber", 0f, 0.05f, 0f, 0.98f),
            Surface(9, "Wood", 0f, 0.24f, 0f, 0.95f),
            Surface(10, "Ceramic", 0f, 0.82f, 0f, 1f),
            Surface(11, "Screen", 0f, 0.62f, 0.20f, 1f),
            Surface(12, "LED", 0f, 0.68f, 0.60f, 1f),
            Surface(13, "Neon", 0f, 0.45f, 1f, 1f),
            Surface(14, "Rough Metal", 1f, 0.24f, 0f, 0.95f),
            Surface(15, "Polished Metal", 1f, 0.92f, 0f, 1f),
            // Artistic starting points, not measured material identities. See SURFACE_CATALOG.md.
            Surface(16, "Asphalt Dry", 0f, 0.04f, 0f, 1f),
            Surface(17, "Brick", 0f, 0.12f, 0f, 1f),
            Surface(18, "Plaster", 0f, 0.08f, 0f, 1f),
            Surface(19, "Stone Rough", 0f, 0.18f, 0f, 1f),
            Surface(20, "Stone Honed", 0f, 0.42f, 0f, 1f),
            Surface(21, "Stone Polished", 0f, 0.88f, 0f, 1f),
            Surface(22, "Concrete Sealed", 0f, 0.38f, 0f, 1f),
            Surface(23, "Terracotta", 0f, 0.20f, 0f, 1f),
            Surface(24, "Tile Satin", 0f, 0.55f, 0f, 1f),
            Surface(25, "Wood Raw", 0f, 0.16f, 0f, 1f),
            Surface(26, "Wood Oiled", 0f, 0.40f, 0f, 1f),
            Surface(27, "Wood Varnished", 0f, 0.72f, 0f, 1f),
            Surface(28, "Leather Matte", 0f, 0.30f, 0f, 1f),
            Surface(29, "Leather Polished", 0f, 0.60f, 0f, 1f),
            Surface(30, "Vinyl", 0f, 0.50f, 0f, 1f),
            Surface(31, "Rubber Smooth", 0f, 0.32f, 0f, 1f),
            Surface(32, "Plastic Satin", 0f, 0.48f, 0f, 1f),
            Surface(33, "Paint Matte", 0f, 0.22f, 0f, 1f),
            Surface(34, "Paint Satin", 0f, 0.52f, 0f, 1f),
            Surface(35, "Paint Gloss", 0f, 0.86f, 0f, 1f),
            Surface(36, "Metal Cast", 1f, 0.14f, 0f, 1f),
            Surface(37, "Metal Satin", 1f, 0.54f, 0f, 1f),
            Surface(38, "Metal Machined", 1f, 0.74f, 0f, 1f),
            Surface(39, "Metal Mirror", 1f, 0.98f, 0f, 1f),
            Surface(40, "Rust", 0f, 0.13f, 0f, 1f),
            Surface(41, "Emissive Indicator", 0f, 0.55f, 0.35f, 1f),
            Surface(42, "Emissive Panel", 0f, 0.25f, 0.75f, 1f),
            Surface(43, "Emissive Tube", 0f, 0.80f, 1f, 1f),
            new(44, "Glass", VoxelSurfaceRenderClass.Transparent, 0f, .9f, 0f, 1f, .25f)
        };

        private static VoxelSurfaceDefinition Surface(int id, string displayName,
            float metallic, float smoothness, float emission, float occlusionMultiplier) =>
            new(id, displayName, VoxelSurfaceRenderClass.Opaque,
                metallic, smoothness, emission, occlusionMultiplier);
    }
}
