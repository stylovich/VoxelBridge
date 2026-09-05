using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace LocalModels.VoxelBridge.Tests
{
    public sealed class VoxelSemanticMesherTests
    {
        private static VoxelGrid Solid(Vector3Int size, ushort semantic = 0)
        {
            var grid = new VoxelGrid(size, new Vector3(-1, 2, 3), 0.032f, true);
            for (int i = 0; i < grid.Occupied.Length; i++)
            { grid.Occupied[i] = true; grid.SemanticIds[i] = semantic; }
            return grid;
        }

        [TestCase(0, 0)]
        [TestCase(255, 255)]
        [TestCase(12, 197)]
        public void SolidCuboid_HasSixQuadsWithExactIdsAndPhysicalBounds(int color, int surface)
        {
            VoxelGrid grid = Solid(new Vector3Int(3, 4, 5), VoxelSemanticEncoding.Pack(color, surface));
            Mesh mesh = VoxelSemanticMesher.Build(grid, true);
            try
            {
                Assert.That(mesh.vertexCount, Is.EqualTo(24));
                Assert.That(mesh.triangles.Length, Is.EqualTo(36));
                Assert.That(mesh.subMeshCount, Is.EqualTo(1));
                Assert.That(mesh.bounds.min, Is.EqualTo(grid.Origin).Using(Vector3Comparer));
                Assert.That(mesh.bounds.size, Is.EqualTo((Vector3)grid.Size * grid.VoxelSize).Using(Vector3Comparer));
                Assert.That(mesh.uv.All(value => value == new Vector2(color, 0)), Is.True);
                Assert.That(mesh.uv4.All(value => value == new Vector2(surface, 0)), Is.True);
                Assert.That(mesh.HasVertexAttribute(VertexAttribute.Color), Is.False);
                Assert.That(mesh.HasVertexAttribute(VertexAttribute.TexCoord1), Is.False);
                Assert.That(mesh.HasVertexAttribute(VertexAttribute.TexCoord2), Is.False);
                Vector3[] vertices = mesh.vertices, normals = mesh.normals;
                int[] triangles = mesh.triangles;
                for (int i = 0; i < triangles.Length; i += 3)
                {
                    int a = triangles[i], b = triangles[i + 1], c = triangles[i + 2];
                    Assert.That(Vector3.Dot(Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]).normalized, normals[a]), Is.GreaterThan(0.999f));
                }
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        private static readonly IEqualityComparer<Vector3> Vector3Comparer = new ApproximateVector3();
        private sealed class ApproximateVector3 : IEqualityComparer<Vector3>
        {
            public bool Equals(Vector3 a, Vector3 b) => Vector3.Distance(a, b) < 0.00001f;
            public int GetHashCode(Vector3 value) => 0;
        }

        [TestCase(1, 0)]
        [TestCase(0, 1)]
        public void DifferentSemanticPairs_SplitExternalFacesButNotTheInternalBoundary(int color, int surface)
        {
            VoxelGrid grid = Solid(new Vector3Int(2, 1, 1));
            grid.SemanticIds[1] = VoxelSemanticEncoding.Pack(color, surface);
            Mesh mesh = VoxelSemanticMesher.Build(grid, false);
            try
            {
                Assert.That(mesh.vertexCount, Is.EqualTo(40));
                Assert.That(Area(mesh), Is.EqualTo(10 * grid.VoxelSize * grid.VoxelSize).Within(0.000001f));
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        [TestCase(true, 24)]
        [TestCase(false, 48)]
        public void EnclosedCavity_RespectsVisibilityOption(bool hide, int expectedVertices)
        {
            VoxelGrid grid = Solid(new Vector3Int(3, 3, 3));
            grid.Occupied[grid.Index(1, 1, 1)] = false;
            Mesh mesh = VoxelSemanticMesher.Build(grid, hide);
            try { Assert.That(mesh.vertexCount, Is.EqualTo(expectedVertices)); }
            finally { Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void OpenCavity_IsNotRemoved()
        {
            VoxelGrid grid = Solid(new Vector3Int(3, 3, 3));
            grid.Occupied[grid.Index(1, 1, 1)] = grid.Occupied[grid.Index(1, 1, 0)] = false;
            Mesh open = VoxelSemanticMesher.Build(grid, true), all = VoxelSemanticMesher.Build(grid, false);
            try { Assert.That(Area(open), Is.EqualTo(Area(all)).Within(0.000001f)); }
            finally { Object.DestroyImmediate(open); Object.DestroyImmediate(all); }
        }

        [Test]
        public void RandomGrid_GreedyAreaMatchesVisibleUnitFaces()
        {
            var random = new System.Random(147);
            VoxelGrid grid = Solid(new Vector3Int(7, 9, 5));
            for (int i = 0; i < grid.Occupied.Length; i++)
            { grid.Occupied[i] = random.Next(2) == 1; grid.SemanticIds[i] = (ushort)random.Next(65536); }
            Mesh mesh = VoxelSemanticMesher.Build(grid, false);
            try
            {
                int faceCount = 0;
                for (int i = 0; i < grid.Occupied.Length; i++)
                {
                    if (!grid.Occupied[i]) continue;
                    grid.Coordinates(i, out int x, out int y, out int z);
                    var p = new Vector3Int(x, y, z);
                    for (int axis = 0; axis < 3; axis++)
                    for (int sign = -1; sign <= 1; sign += 2)
                    {
                        var q = p; q[axis] += sign;
                        if (q[axis] < 0 || q[axis] >= grid.Size[axis] || !grid.Occupied[grid.Index(q.x, q.y, q.z)]) faceCount++;
                    }
                }
                Assert.That(Area(mesh), Is.EqualTo(faceCount * grid.VoxelSize * grid.VoxelSize).Within(0.00001f));
                int[] indices = mesh.triangles;
                Vector2[] colors = mesh.uv, surfaces = mesh.uv4;
                for (int i = 0; i < indices.Length; i += 3)
                    for (int j = 1; j <= 2; j++)
                    {
                        Assert.That(colors[indices[i + j]], Is.EqualTo(colors[indices[i]]));
                        Assert.That(surfaces[indices[i + j]], Is.EqualTo(surfaces[indices[i]]));
                    }
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void InvalidInputsAndBudgets_AreRejectedWithoutChangingGrid()
        {
            VoxelGrid grid = Solid(Vector3Int.one);
            Assert.Throws<InvalidDataException>(() => VoxelSemanticMesher.Build(grid, false, maximumQuads: 5));
            Assert.Throws<OperationCanceledException>(() => VoxelSemanticMesher.Build(grid, true, _ => throw new OperationCanceledException()));
            Assert.That(grid.Occupied[0], Is.True);
            grid.Occupied[0] = false;
            Assert.Throws<InvalidDataException>(() => VoxelSemanticMesher.Build(grid, false));
            Assert.Throws<ArgumentException>(() => VoxelSemanticMesher.Build(new VoxelGrid(Vector3Int.one, Vector3.zero, 1), false));
            Assert.Throws<InvalidDataException>(() => VoxelSemanticMesher.ValidateGrid(new Vector3Int(8_000_000, 8_000_000, 8_000_000), Vector3.zero, 1));
            Assert.Throws<InvalidDataException>(() => VoxelSemanticMesher.ValidateGrid(Vector3Int.one, Vector3.zero, float.NaN));
        }

        [Test]
        public void NonOpaqueAndUnknownSurfaces_AreRejected()
        {
            var palette = ScriptableObject.CreateInstance<VoxelSurfacePalette>();
            try
            {
                palette.MutableEntries.Clear();
                palette.MutableEntries.Add(new VoxelSurfaceDefinition(0, "Glass", VoxelSurfaceRenderClass.Transparent, 0, 0, 0, 1));
                Assert.Throws<InvalidDataException>(() => VoxelProductionExporter.ValidateSurfaces(Solid(Vector3Int.one), palette));
                Assert.Throws<InvalidDataException>(() => VoxelProductionExporter.ValidateSurfaces(Solid(Vector3Int.one, 65535), palette));
            }
            finally { Object.DestroyImmediate(palette); }
        }

        [Test]
        public void ProductionShader_HasHdrpPassesAndCompiles()
        {
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(VoxelProductionExporter.ShaderPath);
            Assert.That(shader, Is.Not.Null);
            Assert.That(ShaderUtil.ShaderHasError(shader), Is.False);
            var material = new Material(shader);
            try
            {
                Assert.That(material.FindPass("GBuffer"), Is.GreaterThanOrEqualTo(0));
                Assert.That(material.FindPass("ShadowCaster"), Is.GreaterThanOrEqualTo(0));
                Assert.That(material.FindPass("DepthOnly"), Is.GreaterThanOrEqualTo(0));
                Assert.That(material.HasProperty("_PaletteColor"), Is.True);
                Assert.That(material.HasProperty("_PaletteSurface"), Is.True);
            }
            finally { Object.DestroyImmediate(material); }
        }

        [Test]
        public void Export_ReadsChunkedVoxAndSharesMaterialWithoutPreviewDependencies()
        {
            string folder = "Assets/VoxelProductionTest_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder));
            string createdMaterialPath = null;
            try
            {
                var colors = ScriptableObject.CreateInstance<VoxelColorPalette>();
                var surfaces = ScriptableObject.CreateInstance<VoxelSurfacePalette>();
                AssetDatabase.CreateAsset(colors, folder + "/Colors.asset");
                AssetDatabase.CreateAsset(surfaces, folder + "/Surfaces.asset");
                Assert.That(VoxelPaletteLutGenerator.TryRebuild(colors, out _, out string error), Is.True, error);
                Assert.That(VoxelPaletteLutGenerator.TryRebuild(surfaces, out _, out error), Is.True, error);
                VoxelGrid grid = Solid(new Vector3Int(20, 3, 2));
                QuantizedVoxels quantized = VoxelSemanticQuantizer.Quantize(grid, colors, surfaces);
                string voxPath = folder + "/Source.vox";
                VoxWriteResult write = VoxelChunkedVoxWriter.Write(VoxelLodPipeline.AssetPathToAbsolute(voxPath), grid, quantized, 16);
                Assert.That(write.Chunks.Length, Is.GreaterThan(1));
                Assert.That(VoxelSemanticTransport.TryCreateMetadata(quantized.SemanticSlots, colors, surfaces,
                    out VoxelSemanticMetadata semantics, out error), Is.True, error);
                var metadata = new VoxelBridgeMetadata
                {
                    formatVersion = 4, sourceName = "Source", voxelSize = grid.VoxelSize,
                    gridOrigin = grid.Origin, unityGridSize = grid.Size, chunks = write.Chunks, semantic = semantics
                };
                File.WriteAllText(VoxelLodPipeline.AssetPathToAbsolute(VoxelImporterIntegration.GetMetadataAssetPath(voxPath)), JsonUtility.ToJson(metadata));
                string output = folder + "/Output";
                Assert.Throws<OperationCanceledException>(() => VoxelProductionExporter.Export(voxPath, output, _ => throw new OperationCanceledException()));
                Assert.That(AssetDatabase.IsValidFolder(output), Is.False);
                GameObject first = VoxelProductionExporter.Export(voxPath, output);
                Material shared = first.GetComponent<MeshRenderer>().sharedMaterial;
                createdMaterialPath = AssetDatabase.GetAssetPath(shared);
                GameObject second = VoxelProductionExporter.Export(voxPath, output);
                Assert.That(second.GetComponent<MeshRenderer>().sharedMaterial, Is.EqualTo(shared));
                Assert.That(first.GetComponent<MeshFilter>().sharedMesh.vertexCount, Is.EqualTo(24));
                Assert.That(first.GetComponent<MeshFilter>().sharedMesh.bounds.min, Is.EqualTo(grid.Origin).Using(Vector3Comparer));
                Assert.That(AssetDatabase.GetAssetPath(first), Is.Not.EqualTo(AssetDatabase.GetAssetPath(second)));
                string[] dependencies = AssetDatabase.GetDependencies(AssetDatabase.GetAssetPath(first));
                Assert.That(dependencies.Any(path => path.EndsWith(".vox") || path.Contains("VoxelImporter") ||
                    (path.StartsWith("Assets/") && path.EndsWith(".cs"))), Is.False);
                Assert.That(shared.GetTexture("_PaletteColor"), Is.EqualTo(colors.GeneratedLut));
                Assert.That(shared.GetTexture("_PaletteSurface"), Is.EqualTo(surfaces.GeneratedLut));
            }
            finally
            {
                if (createdMaterialPath != null) AssetDatabase.DeleteAsset(createdMaterialPath);
                AssetDatabase.DeleteAsset(folder);
            }
        }

        private static float Area(Mesh mesh)
        {
            Vector3[] vertices = mesh.vertices;
            int[] indices = mesh.triangles;
            float area = 0;
            for (int i = 0; i < indices.Length; i += 3)
                area += Vector3.Cross(vertices[indices[i + 1]] - vertices[indices[i]], vertices[indices[i + 2]] - vertices[indices[i]]).magnitude * 0.5f;
            return area;
        }
    }
}
