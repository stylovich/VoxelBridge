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

        [TestCase(false)]
        [TestCase(true)]
        public void ReplaceMeshData_RefreshesUploadedGpuBuffers(bool changeVertexCount)
        {
            Mesh target = VoxelSemanticMesher.Build(Solid(Vector3Int.one, VoxelSemanticEncoding.Pack(25, 0)), false);
            var grid = Solid(changeVertexCount ? new Vector3Int(2, 1, 1) : Vector3Int.one, VoxelSemanticEncoding.Pack(54, 1));
            if (changeVertexCount) grid.SemanticIds[1] = VoxelSemanticEncoding.Pack(12, 2);
            Mesh source = VoxelSemanticMesher.Build(grid, false);
            try
            {
                AssertGpuMatchesMesh(target); // Upload the old mesh before replacement, as a visible scene instance does.
                VoxelProductionExporter.RegisterMeshUndo(target);
                VoxelProductionExporter.ReplaceMeshData(source, target);
                Assert.That(target.vertexCount, Is.EqualTo(source.vertexCount));
                AssertGpuMatchesMesh(target);
                Undo.FlushUndoRecordObjects();
                Undo.PerformUndo();
                Assert.That(target.uv[0].x, Is.EqualTo(25));
                AssertGpuMatchesMesh(target);
                Undo.PerformRedo();
                Assert.That(target.vertexCount, Is.EqualTo(source.vertexCount));
                AssertGpuMatchesMesh(target);
            }
            finally
            {
                Undo.ClearUndo(target);
                Object.DestroyImmediate(target);
                Object.DestroyImmediate(source);
            }
        }

        private static void AssertGpuMatchesMesh(Mesh mesh)
        {
            int stream = mesh.GetVertexAttributeStream(VertexAttribute.TexCoord0);
            int stride = mesh.GetVertexBufferStride(stream);
            int colorOffset = mesh.GetVertexAttributeOffset(VertexAttribute.TexCoord0) / sizeof(float);
            int surfaceOffset = mesh.GetVertexAttributeOffset(VertexAttribute.TexCoord3) / sizeof(float);
            int positionOffset = mesh.GetVertexAttributeOffset(VertexAttribute.Position) / sizeof(float);
            using (var buffer = mesh.GetVertexBuffer(stream))
            {
                Assert.That(buffer.count, Is.EqualTo(mesh.vertexCount), "GPU vertex count is stale.");
                var values = new float[mesh.vertexCount * stride / sizeof(float)];
                buffer.GetData(values);
                Vector2[] colors = mesh.uv, surfaces = mesh.uv4;
                Vector3[] vertices = mesh.vertices;
                for (int i = 0; i < mesh.vertexCount; i++)
                {
                    int start = i * stride / sizeof(float);
                    Assert.That(values[start + colorOffset], Is.EqualTo(colors[i].x), $"GPU ColorID at vertex {i}.");
                    Assert.That(values[start + surfaceOffset], Is.EqualTo(surfaces[i].x), $"GPU SurfaceID at vertex {i}.");
                    for (int axis = 0; axis < 3; axis++)
                        Assert.That(values[start + positionOffset + axis], Is.EqualTo(vertices[i][axis]));
                }
            }
            using (var buffer = mesh.GetIndexBuffer())
            {
                int[] triangles = mesh.triangles;
                Assert.That(buffer.count, Is.EqualTo(triangles.Length), "GPU index count is stale.");
                var bytes = new byte[buffer.count * buffer.stride];
                buffer.GetData(bytes);
                for (int i = 0; i < triangles.Length; i++)
                {
                    uint actual = buffer.stride == 2 ? BitConverter.ToUInt16(bytes, i * 2) : BitConverter.ToUInt32(bytes, i * 4);
                    Assert.That(actual, Is.EqualTo(triangles[i]));
                }
            }
        }

        [Test]
        public void ProductionShader_HasHdrpPassesAndNoImportErrors()
        {
            Shader shader = AssetDatabase.LoadAssetAtPath<Shader>(VoxelProductionExporter.ShaderPath);
            Assert.That(shader, Is.Not.Null);
            Assert.That(ShaderUtil.ShaderHasError(shader), Is.False);
            var material = new Material(shader);
            try
            {
                // Inspect the HDRP subshader without relying on a rendered camera to activate the pipeline in batch mode.
                var data = ShaderUtil.GetShaderData(shader);
                bool found = false;
                for (int i = 0; i < data.SerializedSubshaderCount; i++)
                {
                    var subshader = data.GetSerializedSubshader(i);
                    if (subshader.FindTagValue(new ShaderTagId("RenderPipeline")).name != "HDRenderPipeline") continue;
                    var passes = new HashSet<string>();
                    for (int j = 0; j < subshader.PassCount; j++) passes.Add(subshader.GetPass(j).Name);
                    found |= passes.Contains("GBuffer") && passes.Contains("ShadowCaster") && passes.Contains("DepthOnly");
                }
                Assert.That(found, Is.True, "Missing HDRP subshader with GBuffer, ShadowCaster and DepthOnly passes.");
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
                Assert.That(AssetDatabase.GetAssetPath(first), Is.EqualTo(AssetDatabase.GetAssetPath(second)));
                string[] dependencies = AssetDatabase.GetDependencies(AssetDatabase.GetAssetPath(first));
                Assert.That(dependencies.Any(path => path.EndsWith(".vox") || path.Contains("VoxelImporter") ||
                    (path.StartsWith("Assets/") && path.EndsWith(".cs"))), Is.False);
                Assert.That(shared.GetTexture("_PaletteColor"), Is.EqualTo(colors.GeneratedLut));
                Assert.That(shared.GetTexture("_PaletteSurface"), Is.EqualTo(surfaces.GeneratedLut));

                string prefabPath = AssetDatabase.GetAssetPath(first);
                Mesh mesh = first.GetComponent<MeshFilter>().sharedMesh;
                string meshPath = AssetDatabase.GetAssetPath(mesh);
                string prefabGuid = AssetDatabase.AssetPathToGUID(prefabPath);
                Assert.That(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(mesh, out string meshGuid, out long meshId), Is.True);
                Assert.That(VoxelProductionLink.Load(prefabPath).SourcePath, Is.EqualTo(voxPath));
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(first);
                try
                {
                    instance.transform.position = new Vector3(7, 8, 9);
                    instance.transform.localScale = Vector3.one * 2;
                    instance.AddComponent<BoxCollider>();
                    PrefabUtility.RecordPrefabInstancePropertyModifications(instance.transform);
                    grid.Occupied[0] = false;
                    VoxelChunkedVoxWriter.Write(VoxelLodPipeline.AssetPathToAbsolute(voxPath), grid, quantized, 16);
                    Undo.IncrementCurrentGroup();
                    VoxelProductionExporter.Rebuild(prefabPath);
                    Assert.That(mesh.vertexCount, Is.GreaterThan(24));
                    Assert.That(instance.GetComponent<MeshFilter>().sharedMesh, Is.SameAs(mesh));
                    Assert.That(instance.transform.position, Is.EqualTo(new Vector3(7, 8, 9)));
                    Assert.That(instance.transform.localScale, Is.EqualTo(Vector3.one * 2));
                    Assert.That(instance.GetComponent<BoxCollider>(), Is.Not.Null);
                    Assert.That(AssetDatabase.AssetPathToGUID(prefabPath), Is.EqualTo(prefabGuid));
                    Assert.That(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(mesh, out string rebuiltGuid, out long rebuiltId), Is.True);
                    Assert.That(rebuiltGuid, Is.EqualTo(meshGuid));
                    Assert.That(rebuiltId, Is.EqualTo(meshId));
                    Undo.FlushUndoRecordObjects();
                    Undo.PerformUndo();
                    Assert.That(mesh.vertexCount, Is.EqualTo(24));
                    Undo.PerformRedo();
                    Assert.That(mesh.vertexCount, Is.GreaterThan(24));
                    Vector3[] savedVertices = mesh.vertices;
                    Assert.Throws<OperationCanceledException>(() => VoxelProductionExporter.Rebuild(prefabPath, _ => throw new OperationCanceledException()));
                    CollectionAssert.AreEqual(savedVertices, mesh.vertices);
                    File.WriteAllBytes(VoxelLodPipeline.AssetPathToAbsolute(voxPath), new byte[20]);
                    Assert.Throws<InvalidDataException>(() => VoxelProductionExporter.Rebuild(prefabPath));
                    CollectionAssert.AreEqual(savedVertices, mesh.vertices);
                    VoxelChunkedVoxWriter.Write(VoxelLodPipeline.AssetPathToAbsolute(voxPath), grid, quantized, 16);
                    AssetDatabase.ImportAsset(voxPath, ImportAssetOptions.ForceSynchronousImport);
                    Assert.That(AssetDatabase.MoveAsset(voxPath, folder + "/Renamed.vox"), Is.Empty);
                    File.Move(VoxelLodPipeline.AssetPathToAbsolute(VoxelImporterIntegration.GetMetadataAssetPath(voxPath)),
                        VoxelLodPipeline.AssetPathToAbsolute(folder + "/Renamed.voxelbridge.json"));
                    Assert.That(AssetDatabase.MoveAsset(meshPath, folder + "/RenamedMesh.asset"), Is.Empty);
                    Assert.That(AssetDatabase.MoveAsset(prefabPath, folder + "/Renamed.prefab"), Is.Empty);
                    Assert.That(VoxelProductionLink.Load(folder + "/Renamed.prefab").SourcePath, Is.EqualTo(folder + "/Renamed.vox"));
                    VoxelProductionExporter.Rebuild(folder + "/Renamed.prefab");
                    Assert.That(AssetDatabase.TryGetGUIDAndLocalFileIdentifier(instance.GetComponent<MeshFilter>().sharedMesh,
                        out string movedGuid, out long movedId), Is.True);
                    Assert.That(movedGuid, Is.EqualTo(meshGuid));
                    Assert.That(movedId, Is.EqualTo(meshId));
                }
                finally { Object.DestroyImmediate(instance); }
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
