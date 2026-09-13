using System.Collections.Generic;
using NUnit.Framework;
using UnityEngine;

namespace LocalModels.VoxelBridge.Tests
{
    public sealed class VoxelRasterizationTests
    {
        [Test, Combinatorial]
        public void NearPlanarQuad_PreservesEveryFootprintCell(
            [Values(0, 1, 2)] int axis,
            [Values(.024f, .03125f, .0625f, .125f)] float voxelSize,
            [Values(-1, 1)] int sign)
        {
            // Imported road vertices differ by less than 0.1 micrometre. This interval
            // can exceed the flat-face tolerance but be smaller than the upper-bound bias.
            AssertQuadCoverage(axis, voxelSize, sign * 7.450581e-9f, sign * 7.698427e-8f, true);
        }

        [Test, Combinatorial]
        public void FlatQuad_OnEitherSideOfOriginOccupiesOneLayer(
            [Values(0, 1, 2)] int axis, [Values(-1, 1)] int sign)
        {
            AssertQuadCoverage(axis, .03125f, sign * .125f, sign * .125f, true);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void NearPlanarQuad_CrossingBoundaryHasNoHoles(int axis)
        {
            AssertQuadCoverage(axis, .03125f, -6e-8f, 6e-8f, false);
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void SlopedQuad_RetainsItsMultipleLayers(int axis)
        {
            VoxelGrid grid = SampleQuad(axis, .03125f, .03125f * .1f, .03125f * 2.1f);
            Assert.That(Footprint(grid, axis).Count, Is.EqualTo(16));
            var layers = new HashSet<int>();
            for (int z = 0; z < grid.Size.z; z++)
            for (int y = 0; y < grid.Size.y; y++)
            for (int x = 0; x < grid.Size.x; x++)
                if (grid.Occupied[grid.Index(x, y, z)]) layers.Add(new Vector3Int(x, y, z)[axis]);
            Assert.That(layers.Count, Is.GreaterThanOrEqualTo(3), "Real slopes must not be flattened.");
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AlignedBox_KeepsBoundaryFacesInsideWithoutThickening(bool fill)
        {
            // Six exact boundary planes. No scene objects or imported assets are needed.
            const float cell = .03125f;
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            for (int axis = 0; axis < 3; axis++)
            for (int side = 0; side < 2; side++)
                AddQuad(vertices, triangles, axis, -2 * cell, 2 * cell,
                    (side == 0 ? -2 : 2) * cell, (side == 0 ? -2 : 2) * cell);
            var grid = Sample(vertices, triangles, cell, fill);
            Assert.That(grid.CountOccupied(), Is.EqualTo(fill ? 64 : 56));
            for (int z = 0; z < grid.Size.z; z++)
            for (int y = 0; y < grid.Size.y; y++)
            for (int x = 0; x < grid.Size.x; x++)
            {
                if (!grid.Occupied[grid.Index(x, y, z)]) continue;
                Vector3 center = grid.Origin + (new Vector3(x, y, z) + Vector3.one * .5f) * cell;
                for (int axis = 0; axis < 3; axis++)
                    Assert.That(Mathf.Abs(center[axis]), Is.LessThan(2 * cell), "No exterior padding cells.");
            }
        }

        private static void AssertQuadCoverage(int axis, float cell, float low, float high, bool oneLayer)
        {
            var grid = SampleQuad(axis, cell, low, high);
            Assert.That(Footprint(grid, axis).Count, Is.EqualTo(16), "The 4 x 4 footprint must be complete.");
            if (oneLayer) Assert.That(grid.CountOccupied(), Is.EqualTo(16), "Do not thicken the plane.");
        }

        private static HashSet<Vector2Int> Footprint(VoxelGrid grid, int axis)
        {
            var result = new HashSet<Vector2Int>();
            for (int z = 0; z < grid.Size.z; z++)
            for (int y = 0; y < grid.Size.y; y++)
            for (int x = 0; x < grid.Size.x; x++)
            {
                if (!grid.Occupied[grid.Index(x, y, z)]) continue;
                var p = new Vector3Int(x, y, z);
                result.Add(new Vector2Int(p[(axis + 1) % 3], p[(axis + 2) % 3]));
            }
            return result;
        }

        private static VoxelGrid SampleQuad(int axis, float cell, float low, float high)
        {
            var vertices = new List<Vector3>();
            var triangles = new List<int>();
            AddQuad(vertices, triangles, axis, 0, cell * 4, low, high);
            return Sample(vertices, triangles, cell, false);
        }

        private static void AddQuad(List<Vector3> vertices, List<int> triangles, int axis,
            float from, float to, float low, float high)
        {
            int start = vertices.Count;
            for (int i = 0; i < 4; i++)
            {
                var p = Vector3.zero;
                p[axis] = (i & 1) == 0 ? low : high;
                p[(axis + 1) % 3] = (i & 1) == 0 ? from : to;
                p[(axis + 2) % 3] = i < 2 ? from : to;
                vertices.Add(p);
            }
            foreach (int i in new[] { 0, 2, 1, 1, 2, 3 }) triangles.Add(start + i);
        }

        private static VoxelGrid Sample(List<Vector3> vertices, List<int> triangles, float cell, bool fill)
        {
            var mesh = new Mesh();
            try
            {
                mesh.SetVertices(vertices);
                mesh.SetTriangles(triangles, 0);
                return MeshVoxelizer.Voxelize(mesh, new VoxelizationSettings
                {
                    VoxelSize = cell, Padding = 1, ChunkCellSize = 16,
                    ColorMode = VoxelColorMode.SingleColor, FillInterior = fill
                }).Grid;
            }
            finally { Object.DestroyImmediate(mesh); }
        }
    }
}
