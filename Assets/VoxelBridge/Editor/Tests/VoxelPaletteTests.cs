using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace LocalModels.VoxelBridge.Tests
{
    public sealed class VoxelPaletteTests
    {
        [Test]
        public void RecommendedPalettesAreValidAndContainAluminum()
        {
            VoxelColorPalette colors = ScriptableObject.CreateInstance<VoxelColorPalette>();
            VoxelSurfacePalette surfaces = ScriptableObject.CreateInstance<VoxelSurfacePalette>();
            try
            {
                Assert.That(colors.TryValidate(out string colorError), Is.True, colorError);
                Assert.That(surfaces.TryValidate(out string surfaceError), Is.True, surfaceError);
                Assert.That(surfaces.TryGetSurface(7, out VoxelSurfaceDefinition aluminum), Is.True);
                Assert.That(aluminum.DisplayName, Is.EqualTo("Aluminum"));
                Assert.That(aluminum.Metallic, Is.EqualTo(1f));
                Assert.That(aluminum.Smoothness, Is.EqualTo(0.46f));
            }
            finally
            {
                Object.DestroyImmediate(colors);
                Object.DestroyImmediate(surfaces);
            }
        }

        [Test]
        public void ReorderingEntriesDoesNotChangeStableIdLookupOrHash()
        {
            VoxelColorPalette palette = ScriptableObject.CreateInstance<VoxelColorPalette>();
            try
            {
                palette.MutableEntries.Clear();
                palette.MutableEntries.Add(new VoxelColorDefinition(0, "Default", Color.white));
                palette.MutableEntries.Add(new VoxelColorDefinition(200, "Accent", Color.red));
                palette.MutableEntries.Add(new VoxelColorDefinition(7, "Neutral", Color.blue));

                Assert.That(VoxelPaletteLutBuilder.TryBuildColorPixels(
                    palette, out _, out string firstHash, out string firstError), Is.True, firstError);
                palette.MutableEntries.Reverse();
                Assert.That(VoxelPaletteLutBuilder.TryBuildColorPixels(
                    palette, out _, out string secondHash, out string secondError), Is.True, secondError);
                Assert.That(secondHash, Is.EqualTo(firstHash));
                Assert.That(palette.TryGetColor(200, out Color32 accent), Is.True);
                Assert.That(accent, Is.EqualTo((Color32)Color.red));
            }
            finally
            {
                Object.DestroyImmediate(palette);
            }
        }

        [Test]
        public void ValidationRejectsDuplicateOutOfRangeAndMissingDefaultIds()
        {
            VoxelColorPalette palette = ScriptableObject.CreateInstance<VoxelColorPalette>();
            try
            {
                palette.MutableEntries.Clear();
                palette.MutableEntries.Add(new VoxelColorDefinition(0, "Default", Color.white));
                palette.MutableEntries.Add(new VoxelColorDefinition(0, "Duplicate", Color.black));
                Assert.That(palette.TryValidate(out string duplicateError), Is.False);
                StringAssert.Contains("duplicado", duplicateError);

                palette.MutableEntries.Clear();
                palette.MutableEntries.Add(new VoxelColorDefinition(256, "Invalid", Color.white));
                Assert.That(palette.TryValidate(out string rangeError), Is.False);
                StringAssert.Contains("fuera del rango", rangeError);

                palette.MutableEntries.Clear();
                palette.MutableEntries.Add(new VoxelColorDefinition(1, "No Default", Color.white));
                Assert.That(palette.TryValidate(out string defaultError), Is.False);
                StringAssert.Contains("ID 0", defaultError);
            }
            finally
            {
                Object.DestroyImmediate(palette);
            }
        }

        [Test]
        public void UnknownIdDoesNotSilentlyReturnDefault()
        {
            VoxelColorPalette colors = ScriptableObject.CreateInstance<VoxelColorPalette>();
            VoxelSurfacePalette surfaces = ScriptableObject.CreateInstance<VoxelSurfacePalette>();
            try
            {
                Assert.That(colors.TryGetColor(99, out _), Is.False);
                Assert.That(surfaces.TryGetSurface(99, out _), Is.False);
            }
            finally
            {
                Object.DestroyImmediate(colors);
                Object.DestroyImmediate(surfaces);
            }
        }

        [Test]
        public void SurfaceValidationRejectsPbrValuesOutsideUnitRange()
        {
            VoxelSurfacePalette palette = ScriptableObject.CreateInstance<VoxelSurfacePalette>();
            try
            {
                palette.MutableEntries.Clear();
                palette.MutableEntries.Add(new VoxelSurfaceDefinition(
                    0, "Default", VoxelSurfaceRenderClass.Opaque, 1.01f, 0f, 0f, 1f));
                Assert.That(palette.TryValidate(out string error), Is.False);
                StringAssert.Contains("PBR", error);
            }
            finally
            {
                Object.DestroyImmediate(palette);
            }
        }

        [Test]
        public void SurfaceValidationRejectsUnknownRenderClass()
        {
            VoxelSurfacePalette palette = ScriptableObject.CreateInstance<VoxelSurfacePalette>();
            try
            {
                palette.MutableEntries.Clear();
                palette.MutableEntries.Add(new VoxelSurfaceDefinition(
                    0, "Default", (VoxelSurfaceRenderClass)99, 0f, 0f, 0f, 1f));
                Assert.That(palette.TryValidate(out string error), Is.False);
                StringAssert.Contains("clase de render desconocida", error);
            }
            finally
            {
                Object.DestroyImmediate(palette);
            }
        }

        [Test]
        public void BuildersCreateExactFixedSizeLutsAndFillUnusedIdsWithDefault()
        {
            VoxelColorPalette colors = ScriptableObject.CreateInstance<VoxelColorPalette>();
            VoxelSurfacePalette surfaces = ScriptableObject.CreateInstance<VoxelSurfacePalette>();
            try
            {
                colors.MutableEntries.Clear();
                colors.MutableEntries.Add(new VoxelColorDefinition(0, "Default", Color.green));
                colors.MutableEntries.Add(new VoxelColorDefinition(250, "Accent", Color.red));
                Assert.That(VoxelPaletteLutBuilder.TryBuildColorPixels(
                    colors, out Color32[] colorPixels, out _, out string colorError), Is.True, colorError);
                Assert.That(colorPixels, Has.Length.EqualTo(256));
                Assert.That(colorPixels[17], Is.EqualTo((Color32)Color.green));
                Assert.That(colorPixels[250], Is.EqualTo((Color32)Color.red));

                surfaces.MutableEntries.Clear();
                surfaces.MutableEntries.Add(new VoxelSurfaceDefinition(
                    0, "Default", VoxelSurfaceRenderClass.Opaque, 0f, 0.25f, 0.5f, 1f));
                Assert.That(VoxelPaletteLutBuilder.TryBuildSurfacePixels(
                    surfaces, out Color32[] surfacePixels, out _, out string surfaceError), Is.True, surfaceError);
                Assert.That(surfacePixels, Has.Length.EqualTo(256));
                Assert.That(surfacePixels[0], Is.EqualTo(new Color32(0, 64, 128, 255)));
                Assert.That(surfacePixels[255], Is.EqualTo(surfacePixels[0]));
            }
            finally
            {
                Object.DestroyImmediate(colors);
                Object.DestroyImmediate(surfaces);
            }
        }

        [Test]
        public void RemovedIdsRemainReserved()
        {
            VoxelColorPalette palette = ScriptableObject.CreateInstance<VoxelColorPalette>();
            try
            {
                Assert.That(palette.TryAddEntry(out int firstId, out string addError), Is.True, addError);
                Assert.That(firstId, Is.EqualTo(1));
                Assert.That(palette.TryRemoveEntryAt(1, out string removeError), Is.True, removeError);
                Assert.That(palette.TryAddEntry(out int secondId, out addError), Is.True, addError);
                Assert.That(secondId, Is.EqualTo(2));
            }
            finally
            {
                Object.DestroyImmediate(palette);
            }
        }

        [Test]
        public void GeneratorCreatesAssetsWithRequiredTextureSettings()
        {
            string folderName = "VoxelBridgePaletteTestOutput_" + System.Guid.NewGuid().ToString("N");
            string testFolder = "Assets/" + folderName;
            AssetDatabase.CreateFolder("Assets", folderName);
            var colors = ScriptableObject.CreateInstance<VoxelColorPalette>();
            var surfaces = ScriptableObject.CreateInstance<VoxelSurfacePalette>();
            AssetDatabase.CreateAsset(colors, testFolder + "/Colors.asset");
            AssetDatabase.CreateAsset(surfaces, testFolder + "/Surfaces.asset");

            try
            {
                Assert.That(VoxelPaletteLutGenerator.TryRebuild(
                    colors, out Texture2D colorLut, out string colorError), Is.True, colorError);
                Assert.That(VoxelPaletteLutGenerator.TryRebuild(
                    surfaces, out Texture2D surfaceLut, out string surfaceError), Is.True, surfaceError);

                AssertTextureSettings(colorLut, expectSrgb: true);
                AssertTextureSettings(surfaceLut, expectSrgb: false);
                Assert.That(VoxelPaletteLutGenerator.IsCurrent(colors), Is.True);
                Assert.That(VoxelPaletteLutGenerator.IsCurrent(surfaces), Is.True);
            }
            finally
            {
                AssetDatabase.DeleteAsset(testFolder);
                AssetDatabase.Refresh();
            }
        }

        private static void AssertTextureSettings(Texture2D texture, bool expectSrgb)
        {
            Assert.That(texture, Is.Not.Null);
            Assert.That(texture.width, Is.EqualTo(256));
            Assert.That(texture.height, Is.EqualTo(1));
            Assert.That(texture.format, Is.EqualTo(TextureFormat.RGBA32));
            Assert.That(texture.mipmapCount, Is.EqualTo(1));
            Assert.That(texture.filterMode, Is.EqualTo(FilterMode.Point));
            Assert.That(texture.wrapMode, Is.EqualTo(TextureWrapMode.Clamp));
            Assert.That(texture.anisoLevel, Is.EqualTo(0));
            Assert.That(texture.isDataSRGB, Is.EqualTo(expectSrgb));
            Assert.That(texture.isReadable, Is.True);
        }
    }
}
