using System.IO;
using System.Linq;
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
        public void PhysicalGridPlanner_AlignsOriginAndUsesRequestedVoxelSize()
        {
            var bounds = new Bounds(new Vector3(0.37f, 1.11f, -0.44f),
                new Vector3(2.13f, 0.91f, 3.07f));

            VoxelGridPlan plan = VoxelGridPlanner.Create(bounds, 0.1f, 1, 128);

            Assert.That(plan.VoxelSize, Is.EqualTo(0.1f).Within(1e-6f));
            Assert.That(Mathf.Abs(plan.Origin.x / 0.1f - Mathf.Round(plan.Origin.x / 0.1f)),
                Is.LessThan(1e-4f));
            Assert.That(Mathf.Abs(plan.Origin.y / 0.1f - Mathf.Round(plan.Origin.y / 0.1f)),
                Is.LessThan(1e-4f));
            Assert.That(Mathf.Abs(plan.Origin.z / 0.1f - Mathf.Round(plan.Origin.z / 0.1f)),
                Is.LessThan(1e-4f));
            Assert.That(plan.Origin.x, Is.LessThanOrEqualTo(bounds.min.x - 0.099f));
            Assert.That(plan.Origin.y, Is.LessThanOrEqualTo(bounds.min.y - 0.099f));
            Assert.That(plan.Origin.z, Is.LessThanOrEqualTo(bounds.min.z - 0.099f));
        }

        [Test]
        public void ChunkedVoxWriterAndReader_RoundTripGlobalVoxelCoordinates()
        {
            var grid = new VoxelGrid(new Vector3Int(35, 9, 7), new Vector3(-1.2f, 0.3f, 2.1f), 0.1f);
            int[] occupied =
            {
                grid.Index(0, 0, 0), grid.Index(15, 4, 2), grid.Index(16, 4, 2),
                grid.Index(34, 8, 6)
            };
            Color32[] colors =
            {
                new(255, 0, 0, 255), new(0, 255, 0, 255),
                new(0, 0, 255, 255), new(255, 255, 0, 255)
            };
            for (int i = 0; i < occupied.Length; i++)
            {
                grid.Occupied[occupied[i]] = true;
                grid.Colors[occupied[i]] = colors[i];
            }
            QuantizedVoxels quantized = VoxelColorQuantizer.Quantize(grid);
            string path = Path.Combine(Path.GetTempPath(), "VoxelBridgeChunkRoundTrip.vox");
            try
            {
                VoxWriteResult write = VoxelChunkedVoxWriter.Write(path, grid, quantized, 16);
                var metadata = new VoxelBridgeMetadata
                {
                    voxelSize = grid.VoxelSize,
                    gridOrigin = grid.Origin,
                    unityGridSize = grid.Size,
                    chunks = write.Chunks
                };

                VoxelGrid restored = VoxelVolumeReader.Read(path, metadata);

                Assert.That(write.UsesSceneGraph, Is.True);
                Assert.That(write.Chunks.Length, Is.EqualTo(3));
                Assert.That(restored.CountOccupied(), Is.EqualTo(occupied.Length));
                foreach (int index in occupied) Assert.That(restored.Occupied[index], Is.True);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Test]
        public void Downsampler_UsesLargerAlignedCellsAndPreservesOccupiedGroups()
        {
            var source = new VoxelGrid(new Vector3Int(8, 4, 4), Vector3.zero, 0.1f);
            for (int x = 0; x < 4; x++)
            {
                int index = source.Index(x, 1, 1);
                source.Occupied[index] = true;
                source.Colors[index] = new Color32(120, 80, 40, 255);
            }

            VoxelGrid reduced = VoxelGridDownsampler.Downsample(source, 0.2f, 0, 128);

            Assert.That(reduced.VoxelSize, Is.EqualTo(0.2f).Within(1e-6f));
            Assert.That(reduced.CountOccupied(), Is.EqualTo(2));
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
        public void AutomaticLodPipeline_CreatesChunkedVoxFamilyAndLodPrefab()
        {
            if (!VoxelImporterIntegration.IsInstalled)
                Assert.Ignore("Voxel Importer es opcional y no está instalado.");

            const string testRoot = "Assets/VoxelBridgeTestOutput";
            GameObject root = null;
            VoxelStyleProfile profile = null;
            try
            {
                root = new GameObject("PipelineCube");
                GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cube.transform.SetParent(root.transform, false);
                cube.transform.localScale = new Vector3(4f, 2f, 2f);
                profile = ScriptableObject.CreateInstance<VoxelStyleProfile>();
                var serializedProfile = new SerializedObject(profile);
                serializedProfile.FindProperty("baseVoxelSize").floatValue = 0.25f;
                serializedProfile.FindProperty("chunkCellSize").intValue = 16;
                serializedProfile.FindProperty("padding").intValue = 1;
                serializedProfile.FindProperty("fillInterior").boolValue = true;
                SerializedProperty multipliers = serializedProfile.FindProperty("lodMultipliers");
                multipliers.arraySize = 2;
                multipliers.GetArrayElementAtIndex(0).intValue = 1;
                multipliers.GetArrayElementAtIndex(1).intValue = 2;
                serializedProfile.ApplyModifiedPropertiesWithoutUndo();

                var options = new VoxelLodBuildOptions
                {
                    ColorMode = VoxelColorMode.SingleColor,
                    SingleColor = new Color32(180, 120, 60, 255),
                    AlphaCutoff = 0.1f,
                    ExportFolder = testRoot + "/Exports",
                    PrefabFolder = testRoot + "/Prefabs"
                };

                VoxelLodBuildResult build = VoxelLodPipeline.GenerateAutomatic(root, profile, options);

                Assert.That(build.VoxAssetPaths.Length, Is.EqualTo(2));
                Assert.That(File.Exists(VoxelLodPipeline.AssetPathToAbsolute(build.ManifestAssetPath)), Is.True);
                Assert.That(VoxelImporterIntegration.TryLoadMetadata(build.VoxAssetPaths[0],
                    out VoxelBridgeMetadata lod0, out string error), Is.True, error);
                Assert.That(lod0.voxelSize, Is.EqualTo(0.25f).Within(1e-6f));
                Assert.That(lod0.chunks.Length, Is.GreaterThan(1));
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(build.PrefabAssetPath);
                Assert.That(prefab, Is.Not.Null);
                string prefabFamilyFolder = VoxelLodPipeline.NormalizeAssetPath(
                    Path.GetDirectoryName(build.PrefabAssetPath));
                Assert.That(prefabFamilyFolder, Is.Not.EqualTo(options.PrefabFolder));
                Assert.That(prefabFamilyFolder, Does.StartWith(options.PrefabFolder + "/"));
                Assert.That(AssetDatabase.IsValidFolder(prefabFamilyFolder), Is.True);
                Assert.That(prefab.GetComponent<LODGroup>().lodCount, Is.EqualTo(2));
                Renderer[] chunkRenderers = prefab.transform.GetChild(0)
                    .GetComponentsInChildren<Renderer>(true);
                Assert.That(chunkRenderers.Length, Is.GreaterThan(1));
                Bounds renderedBounds = chunkRenderers[0].bounds;
                for (int i = 1; i < chunkRenderers.Length; i++)
                    renderedBounds.Encapsulate(chunkRenderers[i].bounds);
                string chunkDiagnostics = string.Join(" | ", chunkRenderers.Select(renderer =>
                    $"{renderer.name}: pos={renderer.transform.position}, bounds={renderer.bounds}"));
                Assert.That(renderedBounds.size.x, Is.EqualTo(4f).Within(0.51f), chunkDiagnostics);
                Assert.That(renderedBounds.size.y, Is.EqualTo(2f).Within(0.51f));
                Assert.That(renderedBounds.size.z, Is.EqualTo(2f).Within(0.51f));

                string prefabGuid = AssetDatabase.AssetPathToGUID(build.PrefabAssetPath);
                string loosePrefabPath = options.PrefabFolder + "/PipelineCube_VoxelLOD.prefab";
                Assert.That(AssetDatabase.MoveAsset(build.PrefabAssetPath, loosePrefabPath), Is.Empty);
                string manifestAbsolute = VoxelLodPipeline.AssetPathToAbsolute(build.ManifestAssetPath);
                VoxelLodSetManifest looseManifest = JsonUtility.FromJson<VoxelLodSetManifest>(
                    File.ReadAllText(manifestAbsolute));
                looseManifest.prefabAssetPath = loosePrefabPath;
                File.WriteAllText(manifestAbsolute, JsonUtility.ToJson(looseManifest, true));
                AssetDatabase.ImportAsset(build.ManifestAssetPath, ImportAssetOptions.ForceSynchronousImport);
                string organizedPrefabPath = VoxelLodPipeline.RebuildPrefab(
                    build.ManifestAssetPath, options.PrefabFolder);
                Assert.That(organizedPrefabPath, Is.Not.EqualTo(loosePrefabPath));
                Assert.That(VoxelLodPipeline.NormalizeAssetPath(Path.GetDirectoryName(organizedPrefabPath)),
                    Does.StartWith(options.PrefabFolder + "/"));
                Assert.That(AssetDatabase.AssetPathToGUID(organizedPrefabPath), Is.EqualTo(prefabGuid));
                Assert.That(AssetDatabase.LoadMainAssetAtPath(loosePrefabPath), Is.Null);

                VoxelLodBuildResult duplicate = VoxelLodPipeline.GenerateManual(
                    build.VoxAssetPaths[0], profile, 1, VoxelLodGenerationMode.DuplicateParent, options);
                Assert.That(VoxelImporterIntegration.TryLoadMetadata(duplicate.VoxAssetPaths[0],
                    out VoxelBridgeMetadata duplicateMetadata, out error), Is.True, error);
                Assert.That(duplicateMetadata.lodGenerationMode,
                    Is.EqualTo(VoxelLodGenerationMode.DuplicateParent));
                Assert.That(duplicateMetadata.voxelSize, Is.EqualTo(0.25f).Within(1e-6f));
                Assert.That(duplicateMetadata.lodMultiplier, Is.EqualTo(1));
                Assert.That(duplicate.PrefabAssetPath, Is.EqualTo(organizedPrefabPath));

                VoxelLodBuildResult reduced = VoxelLodPipeline.GenerateManual(
                    build.VoxAssetPaths[0], profile, 1, VoxelLodGenerationMode.ReduceParent, options);
                Assert.That(VoxelImporterIntegration.TryLoadMetadata(reduced.VoxAssetPaths[0],
                    out VoxelBridgeMetadata reducedMetadata, out error), Is.True, error);
                Assert.That(reducedMetadata.lodGenerationMode,
                    Is.EqualTo(VoxelLodGenerationMode.ReduceParent));
                Assert.That(reducedMetadata.voxelSize, Is.EqualTo(0.5f).Within(1e-6f));
                Assert.That(reducedMetadata.lodMultiplier, Is.EqualTo(2));
                Assert.That(reducedMetadata.voxelCount, Is.LessThan(lod0.voxelCount));
                Assert.That(reduced.PrefabAssetPath, Is.EqualTo(organizedPrefabPath));
            }
            finally
            {
                if (root != null) Object.DestroyImmediate(root);
                if (profile != null) Object.DestroyImmediate(profile);
                AssetDatabase.DeleteAsset(testRoot);
            }
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
