using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace LocalModels.VoxelBridge.Tests
{
    internal sealed class VoxelBridgeCoreTests
    {
        [Test]
        public void TriangleBoxIntersection_DetectsHitAndMiss()
        {
            Assert.That(MeshVoxelizer.TriangleIntersectsBox(
                new Vector3(-1, 0, -1), new Vector3(1, 0, -1), new Vector3(0, 0, 1),
                Vector3.zero, Vector3.one * 0.1f), Is.True);
            Assert.That(MeshVoxelizer.TriangleIntersectsBox(
                new Vector3(2, 2, 2), new Vector3(3, 2, 2), new Vector3(2, 3, 2),
                Vector3.zero, Vector3.one * 0.5f), Is.False);
        }

        [Test]
        public void Quantizer_UsesOnlyValidPaletteIndices()
        {
            var grid = new VoxelGrid(new Vector3Int(20, 20, 1), Vector3.zero, 1f);
            for (int i = 0; i < grid.Occupied.Length; i++)
            {
                grid.Occupied[i] = true;
                grid.Colors[i] = new Color32((byte)(i * 37), (byte)(i * 73), (byte)(i * 109), 255);
            }

            QuantizedVoxels result = VoxelColorQuantizer.Quantize(grid);

            Assert.That(result.Palette.Length, Is.InRange(1, 255));
            foreach (byte index in result.Indices) Assert.That(index, Is.InRange(1, 255));
        }

        [Test]
        public void VoxelImporterTransform_RestoresOriginalGridCoordinates()
        {
            var metadata = new VoxelBridgeMetadata
            {
                formatVersion = 2,
                voxelSize = 0.125f,
                gridOrigin = new Vector3(-2.25f, -0.5f, 1.75f),
                unityGridSize = new Vector3Int(11, 7, 13)
            };
            VoxelImporterTransform transform = VoxelImporterIntegration.CalculateTransform(metadata);
            Vector3 localOffset = new Vector3(-metadata.unityGridSize.x * 0.5f, 0f,
                -metadata.unityGridSize.z * 0.5f);

            var samples = new[]
            {
                Vector3Int.zero,
                new Vector3Int(10, 6, 12),
                new Vector3Int(3, 2, 9)
            };
            foreach (Vector3Int source in samples)
            {
                Vector3 importerCoordinate = new Vector3(
                    metadata.unityGridSize.x - 1 - source.x,
                    source.y,
                    metadata.unityGridSize.z - 1 - source.z);
                Vector3 cornerA = Vector3.Scale(localOffset + transform.ImportOffset + importerCoordinate,
                    transform.ImportScale);
                Vector3 cornerB = Vector3.Scale(localOffset + transform.ImportOffset + importerCoordinate + Vector3.one,
                    transform.ImportScale);
                Vector3 actualMin = Vector3.Min(cornerA, cornerB);
                Vector3 actualMax = Vector3.Max(cornerA, cornerB);
                Vector3 expectedMin = metadata.gridOrigin + (Vector3)source * metadata.voxelSize;
                Vector3 expectedMax = expectedMin + Vector3.one * metadata.voxelSize;
                Assert.That((actualMin - expectedMin).sqrMagnitude, Is.LessThan(1e-10f),
                    $"La esquina mínima de la celda {source} perdió su transformación de ida y vuelta.");
                Assert.That((actualMax - expectedMax).sqrMagnitude, Is.LessThan(1e-10f),
                    $"La esquina máxima de la celda {source} perdió su transformación de ida y vuelta.");
            }
        }

        [Test]
        public void CavitySetting_IsEnabledForLegacyMetadataAndRespectsVersionTwo()
        {
            Assert.That(VoxelImporterIntegration.ShouldIgnoreCavity(
                new VoxelBridgeMetadata { formatVersion = 1, hideInternalCavities = false }), Is.True);
            Assert.That(VoxelImporterIntegration.ShouldIgnoreCavity(
                new VoxelBridgeMetadata { formatVersion = 2, hideInternalCavities = false }), Is.False);
            Assert.That(VoxelImporterIntegration.ShouldIgnoreCavity(
                new VoxelBridgeMetadata { formatVersion = 2, hideInternalCavities = true }), Is.True);
        }

        [Test]
        public void VoxelImporterPatch_AppliesOnceAndThenReportsAlreadyApplied()
        {
            string source = CreateUnpatchedVoxelImporterFixture();

            VoxelImporterPatchResult firstResult = VoxelImporterCompatibilityPatcher.TryPatchSource(
                source, out string patched, out string firstDetail);
            VoxelImporterPatchResult secondResult = VoxelImporterCompatibilityPatcher.TryPatchSource(
                patched, out string unchanged, out string secondDetail);

            Assert.That(firstResult, Is.EqualTo(VoxelImporterPatchResult.Applied), firstDetail);
            Assert.That(secondResult, Is.EqualTo(VoxelImporterPatchResult.AlreadyApplied), secondDetail);
            Assert.That(unchanged, Is.EqualTo(patched));
            Assert.That(CountOccurrences(patched, "Voxel Bridge normal-scale compatibility patch. BEGIN"),
                Is.EqualTo(2));
            Assert.That(CountOccurrences(patched, "Vector3.Scale(desc.Normal, importScaleSign)"),
                Is.EqualTo(2));
        }

        [Test]
        public void VoxelImporterPatch_RejectsChangedImporterWithoutModifyingSource()
        {
            const string originalStatement =
                "for (int j = 0; j < 4; j++) normals.Add(desc.Normal);";
            string source = CreateUnpatchedVoxelImporterFixture();
            int lastStatement = source.LastIndexOf(originalStatement, System.StringComparison.Ordinal);
            source = source.Remove(lastStatement, originalStatement.Length).Insert(lastStatement,
                "for (int j = 0; j < 4; j++) normals.Add(CalculateNormal(desc));");

            VoxelImporterPatchResult result = VoxelImporterCompatibilityPatcher.TryPatchSource(
                source, out string output, out string detail);

            Assert.That(result, Is.EqualTo(VoxelImporterPatchResult.Conflict));
            Assert.That(output, Is.EqualTo(source));
            Assert.That(detail, Does.Contain("Firma esperada"));
        }

        [Test]
        public void GeneratedVoxMeshes_HaveNormalsAlignedWithTriangleWinding()
        {
            string[] files = Directory.GetFiles(Application.dataPath, "*.vox", SearchOption.AllDirectories);
            int checkedTriangles = 0;
            foreach (string file in files)
            {
                if (!File.Exists(Path.ChangeExtension(file, ".voxelbridge.json"))) continue;

                string assetPath = "Assets" + file.Substring(Application.dataPath.Length).Replace('\\', '/');
                foreach (Object asset in AssetDatabase.LoadAllAssetsAtPath(assetPath))
                {
                    if (!(asset is Mesh mesh)) continue;
                    Vector3[] vertices = mesh.vertices;
                    Vector3[] normals = mesh.normals;
                    int[] triangles = mesh.triangles;
                    Assert.That(normals.Length, Is.EqualTo(vertices.Length), assetPath);

                    for (int i = 0; i < triangles.Length; i += 3)
                    {
                        int a = triangles[i];
                        int b = triangles[i + 1];
                        int c = triangles[i + 2];
                        Vector3 geometricNormal = Vector3.Cross(
                            vertices[b] - vertices[a], vertices[c] - vertices[a]);
                        if (geometricNormal.sqrMagnitude < 1e-12f) continue;

                        Vector3 importedNormal = normals[a] + normals[b] + normals[c];
                        Assert.That(importedNormal.sqrMagnitude, Is.GreaterThan(1e-12f), assetPath);
                        float alignment = Vector3.Dot(geometricNormal.normalized, importedNormal.normalized);
                        Assert.That(alignment, Is.GreaterThan(0.999f),
                            $"{assetPath}: la normal del triángulo {i / 3} apunta hacia el interior.");
                        checkedTriangles++;
                    }
                }
            }

            if (checkedTriangles == 0)
                Assert.Ignore("No hay mallas .vox generadas; Voxel Importer es una integración opcional.");
        }

        [Test]
        public void VoxWriter_WritesExpectedHeaderDimensionsAndVoxelCount()
        {
            var grid = new VoxelGrid(new Vector3Int(2, 3, 4), Vector3.zero, 1f);
            grid.Occupied[grid.Index(0, 0, 0)] = true;
            grid.Occupied[grid.Index(1, 2, 3)] = true;
            grid.Colors[grid.Index(0, 0, 0)] = new Color32(255, 0, 0, 255);
            grid.Colors[grid.Index(1, 2, 3)] = new Color32(0, 255, 0, 255);
            QuantizedVoxels quantized = VoxelColorQuantizer.Quantize(grid);
            string path = Path.Combine(Path.GetTempPath(), "VoxelBridgeCoreTests.vox");

            try
            {
                VoxWriter.Write(path, grid, quantized);
                using (var reader = new BinaryReader(File.OpenRead(path)))
                {
                    Assert.That(new string(reader.ReadChars(4)), Is.EqualTo("VOX "));
                    Assert.That(reader.ReadInt32(), Is.EqualTo(150));
                    Assert.That(new string(reader.ReadChars(4)), Is.EqualTo("MAIN"));
                    Assert.That(reader.ReadInt32(), Is.Zero);
                    int childrenBytes = reader.ReadInt32();
                    Assert.That(childrenBytes, Is.EqualTo(reader.BaseStream.Length - reader.BaseStream.Position));
                    Assert.That(new string(reader.ReadChars(4)), Is.EqualTo("SIZE"));
                    Assert.That(reader.ReadInt32(), Is.EqualTo(12));
                    Assert.That(reader.ReadInt32(), Is.Zero);
                    Assert.That(reader.ReadInt32(), Is.EqualTo(2));
                    Assert.That(reader.ReadInt32(), Is.EqualTo(4));
                    Assert.That(reader.ReadInt32(), Is.EqualTo(3));
                    Assert.That(new string(reader.ReadChars(4)), Is.EqualTo("XYZI"));
                    Assert.That(reader.ReadInt32(), Is.EqualTo(12));
                    Assert.That(reader.ReadInt32(), Is.Zero);
                    Assert.That(reader.ReadInt32(), Is.EqualTo(2));
                }
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        private static string CreateUnpatchedVoxelImporterFixture()
        {
            return
                "private void AddFaceSecondary(VoxelData.FaceArea faceArea)\n" +
                "{\n" +
                "    for (int j = 0; j < 4; j++) normals.Add(desc.Normal);\n" +
                "}\n" +
                "private void AddEditFaceSecondary(VoxelData.FaceArea faceArea)\n" +
                "{\n" +
                "    for (int j = 0; j < 4; j++) normals.Add(desc.Normal);\n" +
                "}\n";
        }

        private static int CountOccurrences(string source, string value)
        {
            int count = 0;
            int index = 0;
            while ((index = source.IndexOf(value, index, System.StringComparison.Ordinal)) >= 0)
            {
                count++;
                index += value.Length;
            }
            return count;
        }
    }
}
