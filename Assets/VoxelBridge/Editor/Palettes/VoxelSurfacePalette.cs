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
            Surface(15, "Polished Metal", 1f, 0.92f, 0f, 1f)
        };

        private static VoxelSurfaceDefinition Surface(int id, string displayName,
            float metallic, float smoothness, float emission, float occlusionMultiplier) =>
            new(id, displayName, VoxelSurfaceRenderClass.Opaque,
                metallic, smoothness, emission, occlusionMultiplier);
    }
}
