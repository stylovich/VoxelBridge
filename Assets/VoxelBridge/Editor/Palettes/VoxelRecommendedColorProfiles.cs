using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace LocalModels.VoxelBridge
{
    internal static class VoxelRecommendedColorProfiles
    {
        internal const string ProfileFolder = VoxelPaletteAssetUtility.PaletteFolder + "/Profiles";

        private sealed class Preset
        {
            public readonly string FileName;
            public readonly string Description;
            public readonly VoxelColorIdRange[] Ranges;
            public readonly int[] AdditionalIds;

            public Preset(string fileName, string description,
                VoxelColorIdRange[] ranges, int[] additionalIds = null)
            {
                FileName = fileName;
                Description = description;
                Ranges = ranges;
                AdditionalIds = additionalIds ?? Array.Empty<int>();
            }
        }

        private static readonly Preset[] Presets =
        {
            new("ColorProfile_All",
                "Acceso a toda la biblioteca maestra activa. Adecuado para modelos que necesitan " +
                "la máxima fidelidad cromática dentro del estilo global.",
                Ranges((0, VoxelRecommendedColorLibrary.BrightLast))),

            new("ColorProfile_UrbanIndustrial",
                "Neutros, tierras, pasteles controlados y acentos de seguridad. Recomendado para " +
                "props urbanos, maquinaria, mobiliario y elementos industriales.",
                Ranges(
                    (VoxelRecommendedColorLibrary.NeutralFirst, VoxelRecommendedColorLibrary.NeutralLast),
                    (VoxelRecommendedColorLibrary.EarthFirst, VoxelRecommendedColorLibrary.PastelLast)),
                BrightEnds()),

            new("ColorProfile_Architecture",
                "Biblioteca cromática amplia con una selección reducida de acentos intensos. " +
                "Recomendado para edificios, interiores y estructuras de fondo.",
                Ranges((0, VoxelRecommendedColorLibrary.PastelLast)),
                BrightEnds()),

            new("ColorProfile_Vehicles",
                "Colores de pintura, señalización, metales neutros y acentos intensos. Excluye la " +
                "banda pastel para conservar siluetas y piezas mecánicas bien diferenciadas.",
                Ranges(
                    (0, VoxelRecommendedColorLibrary.EarthLast),
                    (VoxelRecommendedColorLibrary.BrightFirst, VoxelRecommendedColorLibrary.BrightLast))),

            new("ColorProfile_Nature",
                "Neutros, verdes, cianes, azules de cielo, tierras y pasteles naturales. " +
                "Recomendado para vegetación, terreno y elementos orgánicos estilizados.",
                Ranges(
                    (VoxelRecommendedColorLibrary.NeutralFirst, VoxelRecommendedColorLibrary.NeutralLast),
                    (VoxelRecommendedColorLibrary.GreenCyanFirst, 111),
                    (VoxelRecommendedColorLibrary.EarthFirst, VoxelRecommendedColorLibrary.EarthLast),
                    (172, 183)),
                new[] { 207, 211, 215 }),

            new("ColorProfile_MutedNeon",
                "Base neutra, orgánica y pastel con la banda completa de acentos intensos. " +
                "Recomendado para escenas apagadas con pantallas, LEDs, carteles o neón.",
                Ranges(
                    (VoxelRecommendedColorLibrary.NeutralFirst, VoxelRecommendedColorLibrary.NeutralLast),
                    (VoxelRecommendedColorLibrary.EarthFirst, VoxelRecommendedColorLibrary.BrightLast)))
        };

        internal static IReadOnlyList<string> AssetPaths => Presets
            .Select(preset => ProfileFolder + "/" + preset.FileName + ".asset")
            .ToArray();

        internal static bool TryValidateAssetPaths(out string error)
        {
            foreach (string path in AssetPaths)
            {
                if (!VoxelPaletteLutGenerator.TryValidateAssetType<VoxelColorMappingProfile>(path, out error))
                    return false;
            }
            error = null;
            return true;
        }

        internal static bool TryCreateOrResetAssets(VoxelColorPalette palette,
            out VoxelColorMappingProfile[] profiles, out string error)
        {
            profiles = null;
            if (palette == null)
            {
                error = "The global color palette is not assigned.";
                return false;
            }
            if (!palette.TryValidate(out error) || !TryValidateAssetPaths(out error)) return false;

            try
            {
                VoxelPaletteLutGenerator.EnsureAssetFolder(ProfileFolder);
                var result = new List<VoxelColorMappingProfile>(Presets.Length);
                foreach (Preset preset in Presets)
                {
                    string path = ProfileFolder + "/" + preset.FileName + ".asset";
                    VoxelColorMappingProfile profile =
                        AssetDatabase.LoadAssetAtPath<VoxelColorMappingProfile>(path);
                    bool created = profile == null;
                    if (created)
                        profile = ScriptableObject.CreateInstance<VoxelColorMappingProfile>();
                    if (!created) Undo.RecordObject(profile, "Restore Recommended Color Profile");
                    profile.ConfigureRecommended(
                        palette, preset.Description, preset.Ranges, preset.AdditionalIds);
                    if (!profile.TryValidate(out error))
                    {
                        if (created) UnityEngine.Object.DestroyImmediate(profile);
                        return false;
                    }

                    if (created) AssetDatabase.CreateAsset(profile, path);
                    else EditorUtility.SetDirty(profile);
                    result.Add(profile);
                }

                AssetDatabase.SaveAssets();
                profiles = result.ToArray();
                error = null;
                return true;
            }
            catch (Exception exception)
            {
                error = "Could not create the recommended color profiles: " + exception.Message;
                return false;
            }
        }

        private static VoxelColorIdRange[] Ranges(params (int first, int last)[] ranges) =>
            ranges.Select(range => new VoxelColorIdRange(range.first, range.last)).ToArray();

        private static int[] BrightEnds() =>
            new[] { 195, 199, 203, 207, 211, 215, 219, 223 };
    }
}
