using System.Linq;
using NUnit.Framework;
using UnityEngine;

namespace LocalModels.VoxelBridge.Tests
{
    public sealed class VoxelSurfaceCatalogTests
    {
        private VoxelSurfacePalette palette;

        [SetUp] public void SetUp() => palette = ScriptableObject.CreateInstance<VoxelSurfacePalette>();
        [TearDown] public void TearDown() => Object.DestroyImmediate(palette);

        [Test]
        public void CatalogHasStableIdsAndValidOpaquePresets()
        {
            Assert.That(palette.TryValidate(out string error), Is.True, error);
            CollectionAssert.AreEqual(Enumerable.Range(0, 45), palette.Entries.Select(e => e.Id));
            foreach (var entry in palette.Entries.Skip(16).Take(28))
            {
                Assert.That(entry.RenderClass, Is.EqualTo(VoxelSurfaceRenderClass.Opaque));
                Assert.That(entry.OcclusionMultiplier, Is.EqualTo(1));
                Assert.That(entry.Metallic == 0 || entry.Metallic == 1, Is.True);
            }
            Assert.That(palette.TryGetSurface(44, out var glass), Is.True);
            Assert.That(glass.RenderClass, Is.EqualTo(VoxelSurfaceRenderClass.Transparent));
            Assert.That(glass.Opacity, Is.EqualTo(.25f));
        }

        [Test]
        public void LegacyPresetsKeepTheirExactIdentityAndValues()
        {
            string[] names = { "Default", "Concrete", "Fabric", "Plastic Matte", "Plastic Glossy",
                "Painted Metal", "Steel", "Aluminum", "Rubber", "Wood", "Ceramic", "Screen",
                "LED", "Neon", "Rough Metal", "Polished Metal" };
            float[] smoothness = { 0, .15f, .10f, .28f, .78f, .58f, .66f, .46f,
                .05f, .24f, .82f, .62f, .68f, .45f, .24f, .92f };
            float[] ao = { 1, .9f, .95f, 1, 1, 1, 1, 1, .98f, .95f, 1, 1, 1, 1, .95f, 1 };
            for (int id = 0; id < 16; id++)
            {
                Assert.That(palette.TryGetSurface(id, out var entry), Is.True);
                Assert.That(entry.DisplayName, Is.EqualTo(names[id]));
                Assert.That(entry.Smoothness, Is.EqualTo(smoothness[id]));
                Assert.That(entry.Metallic, Is.EqualTo(id == 6 || id == 7 || id == 14 || id == 15 ? 1 : 0));
                Assert.That(entry.Emission, Is.EqualTo(id == 11 ? .2f : id == 12 ? .6f : id == 13 ? 1f : 0f));
                Assert.That(entry.OcclusionMultiplier, Is.EqualTo(ao[id]));
                Assert.That(entry.RenderClass, Is.EqualTo(VoxelSurfaceRenderClass.Opaque));
            }
        }

        [Test]
        public void AppendPreservesExistingEntriesAndOnlyInvalidatesLutWhenChanged()
        {
            palette.MutableEntries.RemoveAll(e => e.Id >= 16);
            var original = palette.Entries.ToArray();
            palette.SetGeneratedLut(null, "old");
            Assert.That(palette.TryAppendRecommendedEntries(out int added, out string error), Is.True, error);
            Assert.That(added, Is.EqualTo(29));
            for (int i = 0; i < original.Length; i++) Assert.That(palette.Entries[i], Is.SameAs(original[i]));
            Assert.That(palette.GeneratedContentHash, Is.Null);
            palette.SetGeneratedLut(null, "current");
            Assert.That(palette.TryAppendRecommendedEntries(out added, out error), Is.True, error);
            Assert.That(added, Is.Zero);
            Assert.That(palette.GeneratedContentHash, Is.EqualTo("current"));
        }

        [Test]
        public void AppendSkipsOccupiedRetiredAndDuplicateNamesWithoutRenumbering()
        {
            Assert.That(palette.TryRemoveEntryAt(17, out _), Is.True);
            palette.MutableEntries.RemoveAll(e => e.Id >= 16);
            var custom = new VoxelSurfaceDefinition(16, "Custom", VoxelSurfaceRenderClass.Opaque, .3f, .7f, 0, 1);
            palette.MutableEntries.Add(custom);
            palette.MutableEntries.Add(new VoxelSurfaceDefinition(200, "  plaster  ", VoxelSurfaceRenderClass.Opaque, 0, 0, 0, 1));
            Assert.That(palette.TryAppendRecommendedEntries(out int added, out string error), Is.True, error);
            Assert.That(added, Is.EqualTo(26));
            Assert.That(palette.TryGetSurface(16, out var entry), Is.True);
            Assert.That(entry, Is.SameAs(custom));
            Assert.That(palette.TryGetSurface(17, out _), Is.False);
            Assert.That(palette.TryGetSurface(18, out _), Is.False);
            CollectionAssert.AreEqual(new[] { 17 }, palette.RetiredIds);
            Assert.That(palette.TryValidate(out error), Is.True, error);
        }

        [Test]
        public void InvalidPaletteIsRejectedWithoutPartialAppend()
        {
            palette.MutableEntries.RemoveAll(e => e.Id >= 16);
            palette.MutableEntries.Add(new VoxelSurfaceDefinition(0, "Invalid", VoxelSurfaceRenderClass.Opaque, 0, 0, 0, 1));
            var original = palette.Entries.ToArray();
            palette.SetGeneratedLut(null, "unchanged");
            Assert.That(palette.TryAppendRecommendedEntries(out int added, out string error), Is.False);
            Assert.That(error, Is.Not.Empty);
            Assert.That(added, Is.Zero);
            CollectionAssert.AreEqual(original, palette.Entries);
            Assert.That(palette.GeneratedContentHash, Is.EqualTo("unchanged"));
        }
    }
}
