using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace LocalModels.VoxelBridge.Tests
{
    public sealed class VoxelSemanticSelectionTests
    {
        private PreviewRenderUtility preview;
        private VoxelGrid grid;
        private VoxelColorPalette colors;

        [SetUp]
        public void SetUp()
        {
            preview = new PreviewRenderUtility();
            preview.camera.orthographic = true; preview.camera.orthographicSize = 2; preview.camera.aspect = 1;
            preview.camera.nearClipPlane = .1f; preview.camera.farClipPlane = 100;
            preview.camera.transform.SetPositionAndRotation(new Vector3(2, 2, -10), Quaternion.identity);
            grid = new VoxelGrid(new Vector3Int(4, 4, 3), Vector3.zero, 1, true);
            for (int x = 0; x < 4; x++) for (int y = 0; y < 4; y++)
            { Set(x, y, 0, 0, 0); Set(x, y, 2, 0, 0); }
            colors = ScriptableObject.CreateInstance<VoxelColorPalette>();
            colors.MutableEntries.Clear();
            colors.MutableEntries.Add(new VoxelColorDefinition(0, "Red", new Color32(200, 40, 40, 255)));
            colors.MutableEntries.Add(new VoxelColorDefinition(1, "Near Red", new Color32(206, 42, 40, 255)));
            colors.MutableEntries.Add(new VoxelColorDefinition(2, "Further Red", new Color32(212, 44, 40, 255)));
            colors.MutableEntries.Add(new VoxelColorDefinition(3, "Blue", Color.blue));
        }

        [TearDown]
        public void TearDown() { preview?.Cleanup(); UnityEngine.Object.DestroyImmediate(colors); }

        private void Set(int x, int y, int z, int color, int surface)
        {
            int i = grid.Index(x, y, z); grid.Occupied[i] = true;
            grid.SemanticIds[i] = VoxelSemanticEncoding.Pack(color, surface);
        }

        private VoxelSurfaceSelection Job(VoxelSelectionMode mode = VoxelSelectionMode.Replace, IEnumerable<int> original = null) =>
            new(grid, preview.camera, new Vector2(64, 64), mode, original ?? Array.Empty<int>());

        private static void Complete(VoxelSurfaceSelection job)
        {
            int batches = 0;
            while (!job.IsIdle) { job.Step(128); Assert.That(++batches, Is.LessThan(100000)); }
        }

        [Test]
        public void ExactIds_KeepColorAndSurfaceIndependent()
        {
            grid = new VoxelGrid(new Vector3Int(3, 1, 1), Vector3.zero, 1, true);
            Set(0, 0, 0, 0, 0); Set(1, 0, 0, 0, 7); Set(2, 0, 0, 1, 0);
            using var color = Job(); color.Match(0, colors, VoxelSelectionMatch.ColorID, 0, false, false); Complete(color);
            CollectionAssert.AreEquivalent(new[] { 0, 1 }, color.Result);
            using var surface = Job(); surface.Match(0, colors, VoxelSelectionMatch.SurfaceID, 0, false, false); Complete(surface);
            CollectionAssert.AreEquivalent(new[] { 0, 2 }, surface.Result);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Connected_UsesSixNeighborsAndDoesNotCrossOtherColors(bool connected)
        {
            grid = new VoxelGrid(new Vector3Int(5, 2, 1), Vector3.zero, 1, true);
            Set(0, 0, 0, 0, 0); Set(1, 0, 0, 0, 7); Set(2, 0, 0, 1, 0);
            Set(3, 0, 0, 0, 0); Set(4, 0, 0, 0, 0); Set(2, 1, 0, 0, 0);
            using var job = Job(); job.Match(0, colors, VoxelSelectionMatch.ColorID, 0, connected, false); Complete(job);
            CollectionAssert.AreEquivalent(connected ? new[] { 0, 1 } : new[] { 0, 1, 3, 4, 7 }, job.Result);
        }

        [Test]
        public void SimilarColor_UsesExistingOklabScaleAndDoesNotDriftAlongAGradient()
        {
            grid = new VoxelGrid(new Vector3Int(4, 1, 1), Vector3.zero, 1, true);
            for (int i = 0; i < 4; i++) Set(i, 0, 0, i, i);
            float near = VoxelColorPerceptualMatcher.Distance(colors.Entries[0].Color, colors.Entries[1].Color);
            float far = VoxelColorPerceptualMatcher.Distance(colors.Entries[0].Color, colors.Entries[2].Color);
            Assert.That(near, Is.LessThan(far));
            Assert.That(VoxelColorPerceptualMatcher.TryFindNearest(colors.Entries[0].Color, new[] { colors.Entries[1] }, out var match), Is.True);
            Assert.That(near, Is.EqualTo(match.Distance));
            using var job = Job(); job.Match(0, colors, VoxelSelectionMatch.SimilarColor, (near + far) * .5f, true, false); Complete(job);
            CollectionAssert.AreEquivalent(new[] { 0, 1 }, job.Result);
            using var exact = Job(); exact.Match(0, colors, VoxelSelectionMatch.SimilarColor, 0, false, false); Complete(exact);
            CollectionAssert.AreEquivalent(new[] { 0 }, exact.Result);
        }

        [TestCase(true)]
        [TestCase(false)]
        public void Visibility_HiddenLayerRequiresExplicitOptIn(bool visible)
        {
            using var job = Job(); job.Match(0, colors, VoxelSelectionMatch.SurfaceID, 0, false, visible); Complete(job);
            Assert.That(job.Result.Count, Is.EqualTo(visible ? 16 : 32));
            if (visible) Assert.That(job.Result.All(i => i < 16), Is.True);
        }

        [Test]
        public void Visibility_HoleRevealsRearFaceButOffscreenFacesAreExcluded()
        {
            grid.Occupied[grid.Index(1, 1, 0)] = false;
            Assert.That(VoxelSemanticSelection.IsVisible(grid, preview.camera, grid.Index(1, 1, 2)), Is.True);
            Assert.That(VoxelSemanticSelection.IsVisible(grid, preview.camera, grid.Index(2, 2, 2)), Is.False);
            preview.camera.farClipPlane = 11;
            Assert.That(VoxelSemanticSelection.IsVisible(grid, preview.camera, grid.Index(1, 1, 2)), Is.False, "Faces beyond the camera clip range are not visible.");
            preview.camera.farClipPlane = 100;
            preview.camera.transform.position += Vector3.right * 20;
            Assert.That(VoxelSemanticSelection.IsVisible(grid, preview.camera, 0), Is.False);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void Modes_AreBatchedAndCancellationNeverMutatesOriginalState(int mode)
        {
            var original = new HashSet<int> { 0, 32 };
            ushort[] pairs = (ushort[])grid.SemanticIds.Clone();
            using var job = Job((VoxelSelectionMode)mode, original);
            job.Match(0, colors, VoxelSelectionMatch.ColorID, 0, false, true);
            job.Step(1); Assert.That(job.EvaluatedCells, Is.EqualTo(1)); Assert.That(job.IsIdle, Is.False);
            Complete(job);
            Assert.That(job.Result.Count, Is.EqualTo(mode == 0 ? 16 : mode == 1 ? 17 : 1));
            CollectionAssert.AreEquivalent(new[] { 0, 32 }, original);
            CollectionAssert.AreEqual(pairs, grid.SemanticIds);
            job.Dispose(); Assert.That(job.IsIdle, Is.True);
        }

        [Test]
        public void Limit_RejectsLargeMatchesWithoutCommittingAPartialSelection()
        {
            grid = new VoxelGrid(new Vector3Int(513, 256, 1), Vector3.zero, 1, true); Array.Fill(grid.Occupied, true);
            var original = new HashSet<int> { 0 };
            using var job = Job(VoxelSelectionMode.Replace, original); job.Match(0, colors, VoxelSelectionMatch.ColorID, 0, false, false);
            Assert.Throws<InvalidOperationException>(() => Complete(job));
            CollectionAssert.AreEquivalent(new[] { 0 }, original);
        }

        [Test]
        public void InvalidInputs_AreRejectedAndTransactionsCannotMixTools()
        {
            using var job = Job();
            Assert.Throws<ArgumentException>(() => job.Match(-1, colors, VoxelSelectionMatch.ColorID, 0, true, true));
            Assert.Throws<ArgumentException>(() => job.Match(0, colors, VoxelSelectionMatch.SimilarColor, float.NaN, true, true));
            job.Match(0, colors, VoxelSelectionMatch.ColorID, 0, true, false);
            Assert.Throws<InvalidOperationException>(() => job.Brush(Vector2.zero, Vector2.one, 16));
            Assert.Throws<InvalidOperationException>(() => job.Match(0, colors, VoxelSelectionMatch.ColorID, 0, true, true));
        }
    }
}
