using System;
using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;
using System.Linq;

namespace LocalModels.VoxelBridge.Tests
{
    public sealed class VoxelAppearancePreviewTests
    {
        [Test]
        public void Thumbnails_RenderFirstCandidateCacheAndReleaseOwnedImages()
        {
            var palette = ScriptableObject.CreateInstance<VoxelSurfacePalette>();
            Texture2D image;
            try
            {
                using (var cache = new VoxelSurfaceThumbnails())
                {
                    Assert.That(palette.TryGetSurface(11, out var surface), Is.True);
                    image = cache.Get(palette, surface);
                    Assert.That(image.GetPixels32().Distinct().Count(), Is.GreaterThan(20), "The first thumbnail must contain a rendered sphere, not only its background.");
                    Assert.That(cache.Get(palette, surface), Is.SameAs(image));
                    Assert.That(palette.TryGetSurface(7, out var metal), Is.True);
                    var other = cache.Get(palette, metal);
                    Assert.That(other.GetPixels32(), Is.Not.EqualTo(image.GetPixels32()));
                }
                Assert.That(image == null, Is.True);
            }
            finally { UnityEngine.Object.DestroyImmediate(palette); }
        }
        [Test]
        public void Preview_SplitsSelectionBoundariesWithoutChangingVolumeAndReusesGeometry()
        {
            var grid = new VoxelGrid(new Vector3Int(2, 1, 1), Vector3.zero, 1, true);
            for (int i = 0; i < 2; i++) { grid.Occupied[i] = true; grid.SemanticIds[i] = VoxelSemanticEncoding.Pack(12, 5); }
            var pairs = (ushort[])grid.SemanticIds.Clone();
            Mesh generated;
            using (var preview = new VoxelPainterAppearancePreview(grid, new HashSet<int> { 0 }, true))
            {
                generated = preview.Mesh; var vertices = generated.vertices; var triangles = generated.triangles;
                preview.SetSurface(13); preview.SetColor(42); preview.SetSurface(7);
                Assert.That(preview.Mesh, Is.SameAs(generated));
                Assert.That(generated.vertices, Is.EqualTo(vertices)); Assert.That(generated.triangles, Is.EqualTo(triangles));
                Assert.That(grid.SemanticIds, Is.EqualTo(pairs));
                var flags = generated.uv2; var colors = generated.uv; var surfaces = generated.uv4;
                for (int i = 0; i < flags.Length; i++)
                {
                    Assert.That(colors[i].x, Is.EqualTo(flags[i].x == 1 ? 42 : 12));
                    Assert.That(surfaces[i].x, Is.EqualTo(flags[i].x == 1 ? 7 : 5));
                }
                for (int i = 0; i < triangles.Length; i += 3)
                { Assert.That(flags[triangles[i]], Is.EqualTo(flags[triangles[i + 1]])); Assert.That(flags[triangles[i]], Is.EqualTo(flags[triangles[i + 2]])); }
                Assert.Throws<ArgumentOutOfRangeException>(() => preview.SetColor(256));
            }
            Assert.That(generated == null, Is.True);
        }

        [Test]
        public void Preview_CancellationDoesNotMutateGrid()
        {
            var grid = new VoxelGrid(Vector3Int.one, Vector3.zero, 1, true); grid.Occupied[0] = true;
            Assert.Throws<OperationCanceledException>(() => new VoxelPainterAppearancePreview(grid, new HashSet<int> { 0 }, true,
                _ => throw new OperationCanceledException()));
            Assert.That(grid.SemanticIds[0], Is.Zero); Assert.That(grid.Occupied[0], Is.True);
        }
    }
}
