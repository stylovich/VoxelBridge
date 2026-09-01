using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace LocalModels.VoxelBridge.Tests
{
    public sealed class VoxelColorMappingTests
    {
        [Test]
        public void ProfileResolvesActiveIdsFromRangesAndExplicitAccents()
        {
            VoxelColorPalette palette = CreatePalette(
                (0, Color.white), (10, Color.red), (30, Color.green), (90, Color.blue));
            VoxelColorMappingProfile profile = ScriptableObject.CreateInstance<VoxelColorMappingProfile>();
            try
            {
                profile.ConfigureForTests(palette, 8f, 20f);
                profile.MutableAllowedRanges.Clear();
                profile.MutableAllowedRanges.Add(new VoxelColorIdRange(10, 30));
                profile.MutableAdditionalColorIds.Add(90);

                Assert.That(profile.TryGetAllowedColors(
                    out VoxelColorDefinition[] colors, out string error), Is.True, error);
                CollectionAssert.AreEqual(new[] { 10, 30, 90 }, colors.Select(entry => entry.Id));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(profile);
                UnityEngine.Object.DestroyImmediate(palette);
            }
        }

        [Test]
        public void MatcherUsesOklabAndReturnsExactDistanceForSameColor()
        {
            VoxelColorDefinition[] candidates =
            {
                new(4, "Red", new Color32(230, 35, 28, 255)),
                new(9, "Blue", new Color32(25, 45, 225, 255))
            };

            Assert.That(VoxelColorPerceptualMatcher.TryFindNearest(
                new Color32(225, 40, 30, 255), candidates, out VoxelColorMatch nearRed), Is.True);
            Assert.That(nearRed.ColorId, Is.EqualTo(4));
            Assert.That(nearRed.Distance, Is.GreaterThan(0f));

            Assert.That(VoxelColorPerceptualMatcher.TryFindNearest(
                new Color32(230, 35, 28, 255), candidates, out VoxelColorMatch exact), Is.True);
            Assert.That(exact.ColorId, Is.EqualTo(4));
            Assert.That(exact.Distance, Is.EqualTo(0f).Within(0.0001f));
        }

        [Test]
        public void MatcherBreaksEqualDistanceTiesWithLowerStableId()
        {
            Color32 shared = new(50, 100, 150, 255);
            VoxelColorDefinition[] candidates =
            {
                new(80, "High", shared),
                new(3, "Low", shared)
            };

            Assert.That(VoxelColorPerceptualMatcher.TryFindNearest(
                shared, candidates, out VoxelColorMatch match), Is.True);
            Assert.That(match.ColorId, Is.EqualTo(3));
        }

        [Test]
        public void ProfileRejectsInvertedThresholdsAndUnknownExplicitIds()
        {
            VoxelColorPalette palette = CreatePalette((0, Color.white));
            VoxelColorMappingProfile profile = ScriptableObject.CreateInstance<VoxelColorMappingProfile>();
            try
            {
                profile.ConfigureForTests(palette, 20f, 10f);
                Assert.That(profile.TryValidate(out string thresholdError), Is.False);
                StringAssert.Contains("umbrales", thresholdError);

                profile.ConfigureForTests(palette, 8f, 20f);
                profile.MutableAdditionalColorIds.Add(55);
                Assert.That(profile.TryValidate(out string idError), Is.False);
                StringAssert.Contains("no existe", idError);
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(profile);
                UnityEngine.Object.DestroyImmediate(palette);
            }
        }

        [Test]
        public void SemanticMetadataStoresMappingProfileAsAuthoringRecipe()
        {
            string folderName = "VoxelColorMapping_" + Guid.NewGuid().ToString("N");
            string folder = "Assets/" + folderName;
            AssetDatabase.CreateFolder("Assets", folderName);
            VoxelColorPalette colors = CreatePalette((0, Color.white));
            VoxelSurfacePalette surfaces = ScriptableObject.CreateInstance<VoxelSurfacePalette>();
            VoxelColorMappingProfile profile = ScriptableObject.CreateInstance<VoxelColorMappingProfile>();
            profile.Initialize(colors);
            AssetDatabase.CreateAsset(colors, folder + "/Colors.asset");
            AssetDatabase.CreateAsset(surfaces, folder + "/Surfaces.asset");
            AssetDatabase.CreateAsset(profile, folder + "/Profile.asset");
            AssetDatabase.SaveAssets();

            try
            {
                var slot = new VoxelSemanticSlotMetadata
                {
                    slot = 1,
                    colorId = 0,
                    surfaceId = 0,
                    displayColor = Color.white
                };
                Assert.That(VoxelSemanticTransport.TryCreateMetadata(
                    new[] { slot }, colors, surfaces, profile,
                    out VoxelSemanticMetadata metadata, out string error), Is.True, error);
                Assert.That(metadata.colorMappingProfileGuid,
                    Is.EqualTo(AssetDatabase.AssetPathToGUID(folder + "/Profile.asset")));
                Assert.That(metadata.colorMappingProfileAssetPath,
                    Is.EqualTo(folder + "/Profile.asset"));
            }
            finally
            {
                AssetDatabase.DeleteAsset(folder);
                AssetDatabase.Refresh();
            }
        }

        [Test]
        public void VoxDocumentCountsEveryUseOfEachPaletteSlot()
        {
            var grid = new VoxelGrid(new Vector3Int(3, 1, 1), Vector3.zero, 1f);
            for (int index = 0; index < grid.Occupied.Length; index++)
            {
                grid.Occupied[index] = true;
                grid.Colors[index] = index < 2 ? Color.red : Color.blue;
            }
            string path = Path.Combine(Path.GetTempPath(),
                "VoxelColorUsage_" + Guid.NewGuid().ToString("N") + ".vox");
            try
            {
                QuantizedVoxels quantized = VoxelColorQuantizer.Quantize(grid, 2);
                VoxelChunkedVoxWriter.Write(path, grid, quantized, 16);
                VoxelSemanticVoxDocument document = VoxelSemanticVoxDocument.Read(path);

                Assert.That(document.UsedSlots, Has.Length.EqualTo(2));
                CollectionAssert.AreEquivalent(new[] { 1, 2 },
                    document.UsedSlots.Select(slot => document.SlotUsageCounts[slot]));
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        private static VoxelColorPalette CreatePalette(params (int id, Color color)[] entries)
        {
            VoxelColorPalette palette = ScriptableObject.CreateInstance<VoxelColorPalette>();
            palette.MutableEntries.Clear();
            foreach ((int id, Color color) in entries)
                palette.MutableEntries.Add(new VoxelColorDefinition(id, "Color " + id, color));
            return palette;
        }
    }
}
