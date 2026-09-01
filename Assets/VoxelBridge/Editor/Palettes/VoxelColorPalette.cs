using System.Collections.Generic;
using UnityEngine;

namespace LocalModels.VoxelBridge
{
    [CreateAssetMenu(fileName = "VoxelColorPalette",
        menuName = "Voxel Bridge/Paleta global de colores")]
    public sealed class VoxelColorPalette : ScriptableObject
    {
        private const int CurrentDataVersion = 1;

        [SerializeField] private int dataVersion = CurrentDataVersion;
        [SerializeField] private List<VoxelColorDefinition> entries = CreateDefaultEntries();
        [SerializeField, HideInInspector] private List<int> retiredIds = new();
        [SerializeField, HideInInspector] private Texture2D generatedLut;
        [SerializeField, HideInInspector] private string generatedContentHash;

        public IReadOnlyList<VoxelColorDefinition> Entries => entries;
        public Texture2D GeneratedLut => generatedLut;
        public string GeneratedContentHash => generatedContentHash;

        internal IReadOnlyList<int> RetiredIds => retiredIds;
        internal List<VoxelColorDefinition> MutableEntries => entries;

        public bool TryGetColor(int id, out Color32 color)
        {
            color = default;
            bool found = false;
            if (entries == null) return false;

            foreach (VoxelColorDefinition entry in entries)
            {
                if (entry == null || entry.Id != id) continue;
                if (found) return false;
                color = entry.Color;
                found = true;
            }

            return found;
        }

        public bool TryValidate(out string error) =>
            VoxelPaletteValidation.TryValidate(this, out error);

        internal bool EnsureInitialized()
        {
            if (dataVersion >= CurrentDataVersion) return false;
            dataVersion = CurrentDataVersion;
            entries = CreateDefaultEntries();
            retiredIds = new List<int>();
            generatedLut = null;
            generatedContentHash = null;
            return true;
        }

        internal bool TryAddEntry(out int addedId, out string error)
        {
            EnsureCollections();
            for (int id = 1; id <= VoxelPaletteConstants.MaximumId; id++)
            {
                if (ContainsId(id) || retiredIds.Contains(id)) continue;
                entries.Add(new VoxelColorDefinition(id, $"Color {id}", Color.white));
                generatedContentHash = null;
                addedId = id;
                error = null;
                return true;
            }

            addedId = -1;
            error = "No quedan IDs de color disponibles. Los IDs retirados no se reutilizan automáticamente.";
            return false;
        }

        internal bool TryRemoveEntryAt(int index, out string error)
        {
            EnsureCollections();
            if (index < 0 || index >= entries.Count || entries[index] == null)
            {
                error = "La entrada seleccionada no es válida.";
                return false;
            }

            int id = entries[index].Id;
            if (id == VoxelPaletteConstants.DefaultId)
            {
                error = "El ColorID 0 es la entrada predeterminada y no se puede eliminar.";
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

        internal void ResetToDefault()
        {
            dataVersion = CurrentDataVersion;
            entries = CreateDefaultEntries();
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
            foreach (VoxelColorDefinition entry in entries)
            {
                if (entry != null && entry.Id == id) return true;
            }
            return false;
        }

        private void EnsureCollections()
        {
            entries ??= new List<VoxelColorDefinition>();
            retiredIds ??= new List<int>();
        }

        private static List<VoxelColorDefinition> CreateDefaultEntries() => new()
        {
            new VoxelColorDefinition(0, "Default", Color.white)
        };
    }
}
