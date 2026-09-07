using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace LocalModels.VoxelBridge.Tests
{
    public sealed class VoxelSurfaceSelectionTests
    {
        private Camera camera;
        private PreviewRenderUtility preview;
        private VoxelGrid grid;

        [SetUp]
        public void SetUp()
        {
            preview = new PreviewRenderUtility();
            camera = preview.camera;
            camera.enabled = false; camera.orthographic = true; camera.orthographicSize = 2; camera.aspect = 1;
            camera.transform.SetPositionAndRotation(new Vector3(2, 2, -10), Quaternion.identity);
            grid = new VoxelGrid(new Vector3Int(4, 4, 3), Vector3.zero, 1, true);
            for (int x = 0; x < 4; x++) for (int y = 0; y < 4; y++)
            {
                grid.Occupied[grid.Index(x, y, 0)] = true;
                grid.Occupied[grid.Index(x, y, 2)] = true;
            }
        }

        [TearDown]
        public void TearDown() { preview?.Cleanup(); preview = null; camera = null; }

        private VoxelSurfaceSelection Job(VoxelSelectionMode mode = VoxelSelectionMode.Replace, IEnumerable<int> selected = null) =>
            new(grid, camera, new Vector2(64, 64), mode, selected ?? Array.Empty<int>());

        private static void Complete(VoxelSurfaceSelection job)
        {
            int batches = 0;
            while (!job.IsIdle) { job.Step(64); Assert.That(++batches, Is.LessThan(100000)); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Rectangle_SelectsOnlyFrontCellsInEitherDragDirection(bool reverse)
        {
            using var job = Job();
            job.Rectangle(reverse ? Vector2.one * 64 : Vector2.zero, reverse ? Vector2.zero : Vector2.one * 64);
            job.Step(16);
            Assert.That(job.IsIdle, Is.False, "Selection must be processable in bounded batches.");
            Complete(job);
            Assert.That(job.Result.Count, Is.EqualTo(16));
            Assert.That(job.Result.All(i => i < 16), Is.True, "The rear layer must remain unselected.");
            Assert.That(grid.SemanticIds.All(id => id == 0), Is.True, "Selecting must not paint surfaces or colors.");
        }

        [Test]
        public void Rectangle_HoleRevealsOnlyTheNearestCellBehindIt()
        {
            grid.Occupied[grid.Index(1, 1, 0)] = false;
            using var job = Job(); job.Rectangle(Vector2.zero, Vector2.one * 64); Complete(job);
            Assert.That(job.Result.Count, Is.EqualTo(16));
            Assert.That(job.Result.Contains(grid.Index(1, 1, 2)), Is.True);
            Assert.That(job.Result.Count(i => i >= 16), Is.EqualTo(1));
        }

        [Test]
        public void Brush_InterpolatesFastDragsAndHasAdjustableFootprint()
        {
            using var small = Job(); small.Brush(new Vector2(8, 56), new Vector2(56, 56), 1); Complete(small);
            CollectionAssert.AreEquivalent(new[] { 0, 1, 2, 3 }, small.Result);
            using var large = Job(); large.Brush(new Vector2(32, 32), new Vector2(32, 32), 48); Complete(large);
            Assert.That(large.Result.Count, Is.GreaterThan(4));
            Assert.That(large.Result.All(i => i < 16), Is.True);
            using var click = Job(); click.Brush(new Vector2(8, 56), new Vector2(8, 56), 1); Complete(click);
            CollectionAssert.AreEquivalent(new[] { 0 }, click.Result);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void Modes_AreTransactionalAndPreserveOriginalSelection(int modeValue)
        {
            var mode = (VoxelSelectionMode)modeValue;
            var original = new HashSet<int> { 0, 1 };
            using var job = Job(mode, original);
            job.Brush(new Vector2(8, 56), new Vector2(8, 56), 1); Complete(job);
            int[] expected = mode == VoxelSelectionMode.Replace ? new[] { 0 } : mode == VoxelSelectionMode.Add ? new[] { 0, 1 } : new[] { 1 };
            CollectionAssert.AreEquivalent(expected, job.Result);
            CollectionAssert.AreEquivalent(new[] { 0, 1 }, original);
            job.Dispose();
            CollectionAssert.AreEquivalent(new[] { 0, 1 }, original, "Cancelling or disposing must leave the committed selection intact.");
        }

        [Test]
        public void Rasterization_ClipsToViewportAndDeduplicatesOverlappingStrokes()
        {
            using var job = Job();
            job.Rectangle(new Vector2(-100, -100), new Vector2(200, 200)); Complete(job);
            Assert.That(job.Samples, Is.EqualTo(4096));
            job.Brush(Vector2.zero, Vector2.one * 64, 128); Complete(job);
            Assert.That(job.Samples, Is.EqualTo(4096));
            Assert.That(job.Result.Count, Is.EqualTo(16));
        }

        [Test]
        public void Brush_OutsideViewportDoesNotPaintTheClampedBorder()
        {
            using var job = Job();
            job.Brush(new Vector2(-30, 0), new Vector2(-30, 64), 16); Complete(job);
            Assert.That(job.Result, Is.Empty);
            job.Brush(new Vector2(-30, 56), new Vector2(40, 56), 1); Complete(job);
            CollectionAssert.AreEquivalent(new[] { 0, 1, 2 }, job.Result);
        }

        [Test]
        public void SampleLimit_RejectsOversizedAreasWithoutCommittingPartialResults()
        {
            Array.Fill(grid.Occupied, false);
            var original = new HashSet<int>();
            using var job = new VoxelSurfaceSelection(grid, camera, new Vector2(1025, 1025), VoxelSelectionMode.Add, original);
            job.Rectangle(Vector2.zero, new Vector2(1025, 1025));
            Assert.Throws<InvalidOperationException>(() => Complete(job));
            Assert.That(original, Is.Empty);
            Assert.That(job.Samples, Is.EqualTo(VoxelSurfaceSelection.MaximumSamples + 1));
        }

        [Test]
        public void SelectionLimit_RejectsNewCellsWithoutMutatingCommittedState()
        {
            grid = new VoxelGrid(new Vector3Int(VoxelSurfaceEdit.MaximumSelection + 1, 1, 1), Vector3.zero, 1, true);
            Array.Fill(grid.Occupied, true);
            camera.transform.position = new Vector3(VoxelSurfaceEdit.MaximumSelection + .5f, .5f, -10);
            camera.orthographicSize = .5f;
            var original = new HashSet<int>(Enumerable.Range(0, VoxelSurfaceEdit.MaximumSelection));
            using var job = Job(VoxelSelectionMode.Add, original);
            job.Brush(new Vector2(32, 32), new Vector2(32, 32), 1);
            Assert.Throws<InvalidOperationException>(() => job.Step(64));
            Assert.That(original.Count, Is.EqualTo(VoxelSurfaceEdit.MaximumSelection));
            Assert.That(original.Contains(VoxelSurfaceEdit.MaximumSelection), Is.False);
        }

        [Test]
        public void InvalidInputAndLongStrokes_AreBounded()
        {
            using var job = Job();
            Assert.Throws<ArgumentException>(() => job.Brush(new Vector2(float.NaN, 0), Vector2.zero, 16));
            Assert.Throws<ArgumentOutOfRangeException>(() => job.Brush(Vector2.zero, Vector2.zero, 129));
            Assert.Throws<ArgumentOutOfRangeException>(() => job.Step(0));
            Assert.Throws<ArgumentException>(() => new VoxelSurfaceSelection(grid, camera, new Vector2(9000, 64), VoxelSelectionMode.Add, Array.Empty<int>()));
            for (int i = 0; i < 4096; i++) job.Brush(Vector2.zero, Vector2.zero, 1);
            Assert.Throws<InvalidOperationException>(() => job.Brush(Vector2.zero, Vector2.zero, 1));
            job.Dispose(); Assert.That(job.IsIdle, Is.True);
        }
    }
}
