using System;
using System.Collections.Generic;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace LocalModels.VoxelBridge.Tests
{
    public sealed class VoxelPainterVisualizationTests
    {
        [Test]
        public void Isolation_PreservesIndicesAndSourceAndRefreshesOnlyItsSnapshot()
        {
            var grid = new VoxelGrid(new Vector3Int(4, 2, 2), new Vector3(-2, 3, 5), .032f, true);
            for (int i = 0; i < grid.Occupied.Length; i++)
            { grid.Occupied[i] = true; grid.SemanticIds[i] = VoxelSemanticEncoding.Pack(54, 0); }
            var selected = new HashSet<int> { 0, 1 };
            ushort[] before = (ushort[])grid.SemanticIds.Clone();
            var isolated = new VoxelPainterIsolation(grid, selected);
            Mesh last = null;
            try
            {
                selected.Clear();
                Assert.That(isolated.Grid.Size, Is.EqualTo(grid.Size));
                Assert.That(isolated.Grid.Origin, Is.EqualTo(grid.Origin));
                Assert.That(isolated.Grid.VoxelSize, Is.EqualTo(grid.VoxelSize));
                Assert.That(isolated.Grid.CountOccupied(), Is.EqualTo(2));
                Assert.That(isolated.Grid.Occupied[0] && isolated.Grid.Occupied[1], Is.True);
                Assert.That(grid.CountOccupied(), Is.EqualTo(16));
                CollectionAssert.AreEqual(before, grid.SemanticIds);
                Mesh original = isolated.Mesh;
                grid.SemanticIds[1] = VoxelSemanticEncoding.Pack(54, 13);
                isolated.Refresh();
                Assert.That(original == null, Is.True);
                Assert.That(isolated.Grid.SemanticIds[1], Is.EqualTo(grid.SemanticIds[1]));
                Assert.That(isolated.Mesh.uv4.Any(uv => uv.x == 13), Is.True);
                Assert.That(isolated.Mesh.uv.All(uv => uv.x == 54), Is.True);
                Assert.That(isolated.Grid.Occupied[2], Is.False);
                last = isolated.Mesh;
                Assert.Throws<OperationCanceledException>(() => isolated.Refresh(_ => throw new OperationCanceledException()));
                Assert.That(isolated.Mesh, Is.SameAs(last), "Failed meshing must not replace the previous preview.");
            }
            finally { isolated.Dispose(); }
            Assert.That(last == null, Is.True);
        }

        [Test]
        public void Isolation_ExposesCutFacesAndLimitsPickingAndSemanticQueries()
        {
            var grid = new VoxelGrid(new Vector3Int(3, 3, 3), Vector3.zero, 1, true);
            for (int i = 0; i < grid.Occupied.Length; i++)
            { grid.Occupied[i] = true; grid.SemanticIds[i] = VoxelSemanticEncoding.Pack(0, 0); }
            int center = grid.Index(1, 1, 1);
            using var isolated = new VoxelPainterIsolation(grid, new[] { center });
            Assert.That(isolated.Mesh.vertexCount, Is.EqualTo(24), "An originally enclosed voxel exposes all six cut faces.");
            var ray = new Ray(new Vector3(1.5f, 1.5f, -5), Vector3.forward);
            Assert.That(VoxelSurfaceEdit.Pick(grid, ray, out int original), Is.True);
            Assert.That(original, Is.Not.EqualTo(center));
            Assert.That(VoxelSurfaceEdit.Pick(isolated.Grid, ray, out int hit), Is.True);
            Assert.That(hit, Is.EqualTo(center));
            var preview = new PreviewRenderUtility();
            var colors = ScriptableObject.CreateInstance<VoxelColorPalette>();
            try
            {
                preview.camera.transform.SetPositionAndRotation(new Vector3(1.5f, 1.5f, -5), Quaternion.identity);
                preview.camera.nearClipPlane = .1f; preview.camera.farClipPlane = 30;
                using var job = new VoxelSurfaceSelection(isolated.Grid, preview.camera, new Vector2(64, 64), VoxelSelectionMode.Replace, Array.Empty<int>());
                job.Match(center, colors, VoxelSelectionMatch.SurfaceID, 0, false, false);
                while (!job.IsIdle) job.Step(64);
                CollectionAssert.AreEquivalent(new[] { center }, job.Result, "Even Include Hidden cannot select outside the isolation snapshot.");
                var faces = VoxelSelectionOverlay.Build(isolated.Grid, job.Result, true);
                try { Assert.That(faces.Sum(m => m.vertexCount), Is.EqualTo(24)); }
                finally { VoxelSelectionOverlay.Destroy(faces); }
            }
            finally { preview.Cleanup(); UnityEngine.Object.DestroyImmediate(colors); }
        }

        [Test]
        public void Isolation_RejectsInvalidAndOversizedSelections()
        {
            var grid = new VoxelGrid(new Vector3Int(2, 1, 1), Vector3.zero, 1, true); grid.Occupied[0] = true;
            Assert.Throws<ArgumentException>(() => new VoxelPainterIsolation(null, new[] { 0 }));
            Assert.Throws<ArgumentException>(() => new VoxelPainterIsolation(grid, null));
            Assert.Throws<ArgumentException>(() => new VoxelPainterIsolation(grid, Array.Empty<int>()));
            foreach (int bad in new[] { -1, 1, 2 })
                Assert.Throws<ArgumentException>(() => new VoxelPainterIsolation(grid, new[] { 0, bad }));
            Assert.Throws<ArgumentException>(() => new VoxelPainterIsolation(grid, Enumerable.Repeat(0, VoxelSurfaceEdit.MaximumSelection + 1).ToArray()));
        }

        [Test]
        public void DiagnosticColors_AreStableDistinctAndReserveNeutralForDefault()
        {
            var colors = Enumerable.Range(0, 256).Select(VoxelSurfacePainterWindow.DiagnosticSurfaceColor).ToArray();
            Assert.That(colors.Distinct().Count(), Is.EqualTo(256));
            Assert.That(colors[0], Is.EqualTo(new Color(.5f, .5f, .5f)));
            Assert.That(colors[13], Is.EqualTo(VoxelSurfacePainterWindow.DiagnosticSurfaceColor(13)));
            Assert.That(colors[12], Is.Not.EqualTo(colors[13]));
            Assert.Throws<ArgumentOutOfRangeException>(() => VoxelSurfacePainterWindow.DiagnosticSurfaceColor(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => VoxelSurfacePainterWindow.DiagnosticSurfaceColor(256));
        }
    }
}
