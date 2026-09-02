using System;
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
        [SerializeField] private List<VoxelColorDefinition> entries = CreateRecommendedEntries();
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
            entries = CreateRecommendedEntries();
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

        internal void ResetToRecommended()
        {
            dataVersion = CurrentDataVersion;
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

        private static List<VoxelColorDefinition> CreateRecommendedEntries()
        {
            var result = new List<VoxelColorDefinition>(VoxelRecommendedColorLibrary.ActiveEntryCount)
            {
                new(VoxelPaletteConstants.DefaultId, "Default", Color.white)
            };
            int id = 1;

            AddNeutralRamp(result, ref id, "Neutral", 11, 0f, 0.06f, 0.94f);
            AddNeutralRamp(result, ref id, "Warm Gray", 10, 0.08f, 0.12f, 0.88f, 0.12f);
            AddNeutralRamp(result, ref id, "Cool Gray", 10, 0.58f, 0.12f, 0.88f, 0.12f);

            AddChromaticRamp(result, ref id, "Red", 0.00f, 0.22f, 0.96f, 0.84f, 0.66f);
            AddChromaticRamp(result, ref id, "Vermilion", 0.04f, 0.22f, 0.96f, 0.86f, 0.68f);
            AddChromaticRamp(result, ref id, "Orange", 0.09f, 0.22f, 0.96f, 0.84f, 0.62f);
            AddChromaticRamp(result, ref id, "Yellow", 0.15f, 0.24f, 0.98f, 0.78f, 0.54f);

            AddChromaticRamp(result, ref id, "Lime", 0.24f, 0.20f, 0.92f, 0.72f, 0.54f);
            AddChromaticRamp(result, ref id, "Green", 0.34f, 0.18f, 0.90f, 0.76f, 0.56f);
            AddChromaticRamp(result, ref id, "Teal", 0.45f, 0.18f, 0.90f, 0.70f, 0.50f);
            AddChromaticRamp(result, ref id, "Cyan", 0.52f, 0.20f, 0.94f, 0.72f, 0.48f);

            AddChromaticRamp(result, ref id, "Azure", 0.57f, 0.18f, 0.94f, 0.78f, 0.54f);
            AddChromaticRamp(result, ref id, "Blue", 0.63f, 0.16f, 0.92f, 0.82f, 0.58f);
            AddChromaticRamp(result, ref id, "Indigo", 0.70f, 0.16f, 0.90f, 0.78f, 0.54f);
            AddChromaticRamp(result, ref id, "Violet", 0.79f, 0.18f, 0.92f, 0.76f, 0.52f);

            AddChromaticRamp(result, ref id, "Clay", 0.04f, 0.16f, 0.84f, 0.56f, 0.34f);
            AddChromaticRamp(result, ref id, "Brown", 0.08f, 0.12f, 0.78f, 0.64f, 0.42f);
            AddChromaticRamp(result, ref id, "Ochre", 0.12f, 0.16f, 0.86f, 0.56f, 0.34f);
            AddChromaticRamp(result, ref id, "Olive", 0.20f, 0.14f, 0.78f, 0.42f, 0.28f);

            float[] pastelHues = { 0.00f, 0.08f, 0.16f, 0.34f, 0.48f, 0.58f, 0.72f, 0.88f };
            string[] pastelNames =
            {
                "Pastel Red", "Pastel Orange", "Pastel Yellow", "Pastel Green",
                "Pastel Cyan", "Pastel Blue", "Pastel Violet", "Pastel Magenta"
            };
            for (int index = 0; index < pastelHues.Length; index++)
                AddShortRamp(result, ref id, pastelNames[index], pastelHues[index],
                    0.56f, 0.96f, 0.30f, 0.44f);

            float[] brightHues = { 0.00f, 0.05f, 0.15f, 0.28f, 0.43f, 0.52f, 0.62f, 0.82f };
            string[] brightNames =
            {
                "Bright Red", "Bright Orange", "Bright Yellow", "Bright Lime",
                "Bright Green", "Bright Cyan", "Bright Blue", "Bright Magenta"
            };
            for (int index = 0; index < brightHues.Length; index++)
                AddShortRamp(result, ref id, brightNames[index], brightHues[index],
                    0.58f, 1f, 0.96f, 0.82f);

            if (id != VoxelRecommendedColorLibrary.FirstReservedId)
                throw new InvalidOperationException(
                    $"La biblioteca recomendada terminó en el ColorID {id - 1}; se esperaba 223.");
            return result;
        }

        private static void AddNeutralRamp(List<VoxelColorDefinition> target, ref int id,
            string name, int count, float hue, float minimumValue, float maximumValue,
            float saturation = 0f)
        {
            for (int step = 0; step < count; step++)
            {
                float t = count == 1 ? 0f : step / (float)(count - 1);
                Color color = Color.HSVToRGB(hue, saturation, Mathf.Lerp(minimumValue, maximumValue, t));
                target.Add(new VoxelColorDefinition(id++, $"{name} {step + 1:D2}", (Color32)color));
            }
        }

        private static void AddChromaticRamp(List<VoxelColorDefinition> target, ref int id,
            string name, float hue, float minimumValue, float maximumValue,
            float darkSaturation, float lightSaturation)
        {
            AddRamp(target, ref id, name, hue, 8, minimumValue, maximumValue,
                darkSaturation, lightSaturation);
        }

        private static void AddShortRamp(List<VoxelColorDefinition> target, ref int id,
            string name, float hue, float minimumValue, float maximumValue,
            float firstSaturation, float lastSaturation)
        {
            AddRamp(target, ref id, name, hue, 4, minimumValue, maximumValue,
                firstSaturation, lastSaturation);
        }

        private static void AddRamp(List<VoxelColorDefinition> target, ref int id,
            string name, float hue, int count, float minimumValue, float maximumValue,
            float firstSaturation, float lastSaturation)
        {
            for (int step = 0; step < count; step++)
            {
                float t = count == 1 ? 0f : step / (float)(count - 1);
                float saturation = Mathf.Lerp(firstSaturation, lastSaturation, t);
                float value = Mathf.Lerp(minimumValue, maximumValue, t);
                Color color = Color.HSVToRGB(hue, saturation, value);
                target.Add(new VoxelColorDefinition(id++, $"{name} {step + 1:D2}", (Color32)color));
            }
        }
    }

    internal static class VoxelRecommendedColorLibrary
    {
        public const int ActiveEntryCount = 224;
        public const int FirstReservedId = 224;

        public const int NeutralFirst = 0;
        public const int NeutralLast = 31;
        public const int WarmFirst = 32;
        public const int WarmLast = 63;
        public const int GreenCyanFirst = 64;
        public const int GreenCyanLast = 95;
        public const int BlueVioletFirst = 96;
        public const int BlueVioletLast = 127;
        public const int EarthFirst = 128;
        public const int EarthLast = 159;
        public const int PastelFirst = 160;
        public const int PastelLast = 191;
        public const int BrightFirst = 192;
        public const int BrightLast = 223;
    }
}
