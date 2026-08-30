using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace LocalModels.VoxelBridge.Tests
{
    internal sealed class VoxelBridgeFakeImpostorAsset : ScriptableObject
    {
        public Mesh Mesh;
        public Material Material;
    }

    internal sealed class VoxelBridgeCoreTests
    {
        [Test]
        public void ImpostorProfile_DefaultSettingsFitAfterLastVoxelLod()
        {
            VoxelImpostorProfile profile = ScriptableObject.CreateInstance<VoxelImpostorProfile>();
            try
            {
                foreach (VoxelImpostorQuality quality in new[]
                         {
                             VoxelImpostorQuality.Low,
                             VoxelImpostorQuality.Medium,
                             VoxelImpostorQuality.High,
                             VoxelImpostorQuality.Architecture
                         })
                {
                    Assert.That(profile.TryValidate(quality, 0.05f, out string error),
                        Is.True, $"{quality}: {error}");
                    Assert.That(profile.GetSettings(quality).CullScreenHeight, Is.LessThan(0.05f));
                }

                VoxelImpostorSettings low = profile.GetSettings(VoxelImpostorQuality.Low);
                VoxelImpostorSettings medium = profile.GetSettings(VoxelImpostorQuality.Medium);
                VoxelImpostorSettings high = profile.GetSettings(VoxelImpostorQuality.High);
                VoxelImpostorSettings architecture =
                    profile.GetSettings(VoxelImpostorQuality.Architecture);
                Assert.That(low.TextureResolution, Is.EqualTo(512));
                Assert.That(medium.TextureResolution, Is.EqualTo(1024));
                Assert.That(high.TextureResolution, Is.EqualTo(2048));
                Assert.That(architecture.TextureResolution, Is.EqualTo(2048));
                Assert.That(low.Frames, Is.LessThan(medium.Frames));
                Assert.That(medium.Frames, Is.LessThan(high.Frames));
                Assert.That(low.CrossFade, Is.False);
                Assert.That(medium.CrossFade, Is.True);
                Assert.That(high.CullScreenHeight, Is.LessThan(medium.CullScreenHeight));
                Assert.That(architecture.ImpostorType,
                    Is.EqualTo(VoxelImpostorType.HemiOctahedron));
                Assert.That(architecture.MaxVertices, Is.EqualTo(10));
                Assert.That(architecture.CullScreenHeight, Is.EqualTo(0.0005f).Within(1e-7f));
                Assert.That(architecture.FadeTransitionWidth, Is.EqualTo(0.3f).Within(1e-6f));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void ImpostorProfile_RejectsCullAtVoxelTransition()
        {
            VoxelImpostorProfile profile = ScriptableObject.CreateInstance<VoxelImpostorProfile>();
            try
            {
                var serialized = new SerializedObject(profile);
                serialized.FindProperty("medium").FindPropertyRelative("cullScreenHeight")
                    .floatValue = 0.05f;
                serialized.ApplyModifiedPropertiesWithoutUndo();

                Assert.That(profile.TryValidate(
                    VoxelImpostorQuality.Medium, 0.05f, out string error), Is.False);
                Assert.That(error, Does.Contain("menor que la transición"));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void ImpostorProfile_UnspecifiedQualityFallsBackToMedium()
        {
            VoxelImpostorProfile profile = ScriptableObject.CreateInstance<VoxelImpostorProfile>();
            try
            {
                VoxelImpostorSettings unspecified =
                    profile.GetSettings(VoxelImpostorQuality.Unspecified);
                VoxelImpostorSettings medium = profile.GetSettings(VoxelImpostorQuality.Medium);

                Assert.That(unspecified, Is.SameAs(medium));
                Assert.That(VoxelImpostorProfile.GetQualityName(
                    VoxelImpostorQuality.Unspecified), Is.EqualTo("Medio · Equilibrado"));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

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
        public void Voxelizer_FillInteriorFillsClosedCenterWithoutChangingExterior()
        {
            GameObject cube = null;
            try
            {
                cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                var shellSettings = new VoxelizationSettings
                {
                    VoxelSize = 0.2f,
                    ChunkCellSize = 16,
                    Padding = 1,
                    FillInterior = false,
                    ColorMode = VoxelColorMode.SingleColor,
                    SingleColor = new Color32(90, 140, 210, 255)
                };
                VoxelizationResult shell = MeshVoxelizer.Voxelize(cube, shellSettings);
                var filledSettings = new VoxelizationSettings
                {
                    VoxelSize = shellSettings.VoxelSize,
                    ChunkCellSize = shellSettings.ChunkCellSize,
                    Padding = shellSettings.Padding,
                    FillInterior = true,
                    ColorMode = shellSettings.ColorMode,
                    SingleColor = shellSettings.SingleColor
                };
                VoxelizationResult filled = MeshVoxelizer.Voxelize(cube, filledSettings);

                Assert.That(filled.Grid.Size, Is.EqualTo(shell.Grid.Size));
                Assert.That(filled.Grid.CountOccupied(), Is.GreaterThan(shell.Grid.CountOccupied()));
                Vector3Int center = filled.Grid.Size / 2;
                Assert.That(filled.Grid.Occupied[
                    filled.Grid.Index(center.x, center.y, center.z)], Is.True);
            }
            finally
            {
                if (cube != null) Object.DestroyImmediate(cube);
            }
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
        public void VoxelImporterTransform_DoesNotRecenterSceneGraphTwice()
        {
            var metadata = new VoxelBridgeMetadata
            {
                formatVersion = 3,
                voxelSize = 0.025f,
                gridOrigin = new Vector3(-1.125f, -0.05f, -1.875f),
                unityGridSize = new Vector3Int(89, 97, 157),
                importGridOrigin = new Vector3(-1.1f, -0.025f, -1.85f),
                importGridSize = new Vector3Int(87, 95, 155),
                chunks = new[]
                {
                    new VoxelChunkMetadata { gridOffset = Vector3Int.zero },
                    new VoxelChunkMetadata { gridOffset = new Vector3Int(0, 0, 128) }
                }
            };

            VoxelImporterTransform transform = VoxelImporterIntegration.CalculateTransform(metadata);

            Assert.That(VoxelImporterIntegration.UsesSceneGraph(metadata), Is.True);
            Assert.That(transform.ImportOffset.x, Is.EqualTo(44f).Within(1e-4f));
            Assert.That(transform.ImportOffset.y, Is.EqualTo(-2f).Within(1e-4f));
            Assert.That(transform.ImportOffset.z, Is.EqualTo(74f).Within(1e-4f));
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
        public void AutomaticBatchSources_UsesDirectChildrenAsFamiliesAndIncludesNestedMeshes()
        {
            var parent = new GameObject("BatchParent");
            try
            {
                var nestedFamily = new GameObject("NestedFamily");
                nestedFamily.transform.SetParent(parent.transform, false);
                GameObject nestedCube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                nestedCube.name = "NestedGeometry";
                nestedCube.transform.SetParent(nestedFamily.transform, false);

                var inactiveFamily = new GameObject("InactiveFamily");
                inactiveFamily.transform.SetParent(parent.transform, false);
                GameObject inactiveCube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                inactiveCube.transform.SetParent(inactiveFamily.transform, false);
                inactiveFamily.SetActive(false);

                var emptyFamily = new GameObject("EmptyFamily");
                emptyFamily.transform.SetParent(parent.transform, false);

                GameObject[] sources = VoxelLodPipeline.GetAutomaticBatchSources(parent);

                Assert.That(sources, Is.EqualTo(new[] { nestedFamily, inactiveFamily }));
                Assert.That(sources.Contains(nestedCube), Is.False);
                Assert.That(sources.Contains(emptyFamily), Is.False);
            }
            finally
            {
                Object.DestroyImmediate(parent);
            }
        }

        [Test]
        public void AutomaticBatch_ReusesPrefabSourceContinuesAfterFailureAndPlacesSceneInstances()
        {
            if (!VoxelImporterIntegration.IsInstalled)
                Assert.Ignore("Voxel Importer es opcional y no está instalado.");

            const string testRoot = "Assets/VoxelBridgeBatchTestOutput";
            GameObject parent = null;
            GameObject sourceObject = null;
            GameObject placedRoot = null;
            Mesh invalidMesh = null;
            VoxelStyleProfile profile = null;
            try
            {
                VoxelLodPipeline.EnsureAssetFolder(testRoot);
                parent = new GameObject("BatchParent");
                var invalid = new GameObject("InvalidMesh");
                invalid.transform.SetParent(parent.transform, false);
                invalidMesh = new Mesh { name = "InvalidMesh" };
                invalid.AddComponent<MeshFilter>().sharedMesh = invalidMesh;

                sourceObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
                sourceObject.name = "ReusableCube";
                string sourcePrefabPath = testRoot + "/ReusableCube.prefab";
                GameObject sourcePrefab = PrefabUtility.SaveAsPrefabAsset(
                    sourceObject, sourcePrefabPath);
                Object.DestroyImmediate(sourceObject);
                sourceObject = null;
                Assert.That(sourcePrefab, Is.Not.Null);

                var firstInstance = PrefabUtility.InstantiatePrefab(
                    sourcePrefab, parent.transform) as GameObject;
                var modifiedInstance = PrefabUtility.InstantiatePrefab(
                    sourcePrefab, parent.transform) as GameObject;
                Assert.That(firstInstance, Is.Not.Null);
                Assert.That(modifiedInstance, Is.Not.Null);
                firstInstance.transform.localPosition = new Vector3(-2f, 0f, 1f);
                modifiedInstance.transform.localPosition = new Vector3(3f, 0f, -1f);
                GameObject addedGeometry = GameObject.CreatePrimitive(PrimitiveType.Cube);
                addedGeometry.name = "AddedOverrideGeometry";
                addedGeometry.transform.SetParent(modifiedInstance.transform, false);
                addedGeometry.transform.localPosition = Vector3.right;

                VoxelLodBatchSourcePlan[] originalPlans =
                    VoxelLodPipeline.GetAutomaticBatchPlans(parent, new VoxelLodBatchOptions());
                Assert.That(originalPlans, Has.Length.EqualTo(3));
                Assert.That(originalPlans[1].HasPrefabOverrides, Is.False);
                Assert.That(originalPlans[2].HasPrefabOverrides, Is.True);
                Assert.That(originalPlans[1].ConversionSource, Is.EqualTo(sourcePrefab));
                Assert.That(originalPlans[2].ConversionSource, Is.EqualTo(sourcePrefab));
                Assert.That(originalPlans[1].ReuseKey, Is.EqualTo(originalPlans[2].ReuseKey));

                VoxelLodBatchSourcePlan[] separatePlans =
                    VoxelLodPipeline.GetAutomaticBatchPlans(parent, new VoxelLodBatchOptions
                    {
                        ModifiedPrefabHandling =
                            VoxelPrefabOverrideHandling.ConvertInstanceSeparately
                    });
                Assert.That(separatePlans[2].ConversionSource, Is.EqualTo(modifiedInstance));
                Assert.That(separatePlans[1].ReuseKey, Is.Not.EqualTo(separatePlans[2].ReuseKey));

                VoxelLodBatchSourcePlan[] ignoredPlans =
                    VoxelLodPipeline.GetAutomaticBatchPlans(parent, new VoxelLodBatchOptions
                    {
                        ModifiedPrefabHandling = VoxelPrefabOverrideHandling.IgnoreInstance
                    });
                Assert.That(ignoredPlans[2].Ignored, Is.True);

                profile = ScriptableObject.CreateInstance<VoxelStyleProfile>();
                var serializedProfile = new SerializedObject(profile);
                serializedProfile.FindProperty("baseVoxelSize").floatValue = 0.5f;
                serializedProfile.FindProperty("chunkCellSize").intValue = 16;
                serializedProfile.FindProperty("padding").intValue = 1;
                SerializedProperty multipliers = serializedProfile.FindProperty("lodMultipliers");
                multipliers.arraySize = 1;
                multipliers.GetArrayElementAtIndex(0).intValue = 1;
                serializedProfile.ApplyModifiedPropertiesWithoutUndo();

                var options = new VoxelLodBuildOptions
                {
                    ColorMode = VoxelColorMode.SingleColor,
                    SingleColor = new Color32(120, 180, 220, 255),
                    AlphaCutoff = 0.1f,
                    ExportFolder = testRoot + "/Exports"
                };

                VoxelLodBatchBuildResult batch =
                    VoxelLodPipeline.GenerateAutomaticBatch(parent, profile, options);

                Assert.That(batch.Cancelled, Is.False);
                Assert.That(batch.CandidateCount, Is.EqualTo(3));
                Assert.That(batch.Items, Has.Length.EqualTo(3));
                Assert.That(batch.SucceededCount, Is.EqualTo(2));
                Assert.That(batch.FailedCount, Is.EqualTo(1));
                Assert.That(batch.CreatedFamilyCount, Is.EqualTo(1));
                Assert.That(batch.ReusedCount, Is.EqualTo(1));
                Assert.That(batch.Items[0].Source, Is.EqualTo(invalid));
                Assert.That(batch.Items[0].Succeeded, Is.False);
                Assert.That(batch.Items[0].Error, Does.Contain("vértices"));
                Assert.That(batch.Items[1].Source, Is.EqualTo(firstInstance));
                Assert.That(batch.Items[1].Succeeded, Is.True);
                Assert.That(batch.Items[1].Reused, Is.False);
                Assert.That(batch.Items[1].BuildResult.VoxAssetPaths, Has.Length.EqualTo(1));
                Assert.That(batch.Items[2].Source, Is.EqualTo(modifiedInstance));
                Assert.That(batch.Items[2].Succeeded, Is.True);
                Assert.That(batch.Items[2].Reused, Is.True);
                Assert.That(batch.Items[2].BuildResult.PrefabAssetPath,
                    Is.EqualTo(batch.Items[1].BuildResult.PrefabAssetPath));
                Assert.That(AssetDatabase.LoadAssetAtPath<GameObject>(
                    batch.Items[1].BuildResult.PrefabAssetPath), Is.Not.Null);
                Assert.That(VoxelLodBatchRecovery.FindIncompleteFamilies(options.ExportFolder),
                    Is.Empty);
                Assert.That(Directory.GetDirectories(
                    VoxelLodPipeline.AssetPathToAbsolute(options.ExportFolder)),
                    Has.Length.EqualTo(1),
                    "La fuente inválida no debe dejar una carpeta de familia vacía.");

                VoxelLodBatchPlacementResult incompletePlacement =
                    VoxelLodBatchScenePlacement.Place(parent, batch, true);
                placedRoot = incompletePlacement.Root;
                Assert.That(incompletePlacement.OriginalRootDisabled, Is.False);
                Assert.That(parent.activeSelf, Is.True);
                Assert.That(placedRoot.activeSelf, Is.False);
                Undo.PerformUndo();
                Assert.That(placedRoot == null, Is.True);
                placedRoot = null;

                Object.DestroyImmediate(invalid);
                var completeBatch = new VoxelLodBatchBuildResult(
                    batch.Items.Skip(1), 2, false);
                var nonVisualChild = new GameObject("NonVisualChild");
                nonVisualChild.transform.SetParent(parent.transform, false);
                VoxelLodBatchPlacementResult skippedChildPlacement =
                    VoxelLodBatchScenePlacement.Place(parent, completeBatch, true);
                placedRoot = skippedChildPlacement.Root;
                Assert.That(skippedChildPlacement.OriginalRootDisabled, Is.False);
                Assert.That(parent.activeSelf, Is.True);
                Assert.That(placedRoot.activeSelf, Is.False);
                Undo.PerformUndo();
                Assert.That(placedRoot == null, Is.True);
                placedRoot = null;
                Object.DestroyImmediate(nonVisualChild);

                VoxelLodBatchPlacementResult placement =
                    VoxelLodBatchScenePlacement.Place(parent, completeBatch, true);
                placedRoot = placement.Root;
                Assert.That(placement.PlacedCount, Is.EqualTo(2));
                Assert.That(placement.OriginalRootDisabled, Is.True);
                Assert.That(parent.activeSelf, Is.False);
                Assert.That(placedRoot.activeSelf, Is.True);
                Assert.That(placedRoot.transform.childCount, Is.EqualTo(2));
                Assert.That(placedRoot.transform.GetChild(0).localPosition,
                    Is.EqualTo(firstInstance.transform.localPosition));
                Assert.That(placedRoot.transform.GetChild(1).localPosition,
                    Is.EqualTo(modifiedInstance.transform.localPosition));

                Undo.PerformUndo();
                Assert.That(parent.activeSelf, Is.True);
                Assert.That(placedRoot == null, Is.True);
                placedRoot = null;
            }
            finally
            {
                if (placedRoot != null) Object.DestroyImmediate(placedRoot);
                if (parent != null) Object.DestroyImmediate(parent);
                if (sourceObject != null) Object.DestroyImmediate(sourceObject);
                if (invalidMesh != null) Object.DestroyImmediate(invalidMesh);
                if (profile != null) Object.DestroyImmediate(profile);
                AssetDatabase.DeleteAsset(testRoot);
            }
        }

        [Test]
        public void AutomaticBatch_ReusesOriginalSourceForNestedPrefabInstances()
        {
            const string testRoot = "Assets/VoxelBridgeNestedBatchTestOutput";
            GameObject reusableSource = null;
            GameObject containerSource = null;
            GameObject containerInstance = null;
            try
            {
                VoxelLodPipeline.EnsureAssetFolder(testRoot);
                reusableSource = GameObject.CreatePrimitive(PrimitiveType.Cube);
                reusableSource.name = "ReusableNestedPart";
                GameObject reusablePrefab = PrefabUtility.SaveAsPrefabAsset(
                    reusableSource, testRoot + "/ReusableNestedPart.prefab");
                Object.DestroyImmediate(reusableSource);
                reusableSource = null;
                Assert.That(reusablePrefab, Is.Not.Null);

                containerSource = new GameObject("Container");
                var first = PrefabUtility.InstantiatePrefab(
                    reusablePrefab, containerSource.transform) as GameObject;
                var second = PrefabUtility.InstantiatePrefab(
                    reusablePrefab, containerSource.transform) as GameObject;
                Assert.That(first, Is.Not.Null);
                Assert.That(second, Is.Not.Null);
                first.name = "Part_A";
                second.name = "Part_B";
                second.transform.localPosition = Vector3.right * 2f;
                GameObject containerPrefab = PrefabUtility.SaveAsPrefabAsset(
                    containerSource, testRoot + "/Container.prefab");
                Object.DestroyImmediate(containerSource);
                containerSource = null;
                Assert.That(containerPrefab, Is.Not.Null);

                containerInstance = PrefabUtility.InstantiatePrefab(containerPrefab) as GameObject;
                Assert.That(containerInstance, Is.Not.Null);
                VoxelLodBatchSourcePlan[] plans = VoxelLodPipeline.GetAutomaticBatchPlans(
                    containerInstance, new VoxelLodBatchOptions());

                Assert.That(plans, Has.Length.EqualTo(2));
                Assert.That(plans[0].ConversionSource, Is.EqualTo(reusablePrefab));
                Assert.That(plans[1].ConversionSource, Is.EqualTo(reusablePrefab));
                Assert.That(plans[0].ReuseKey, Is.EqualTo(plans[1].ReuseKey));
                Assert.That(plans[0].HasPrefabOverrides, Is.False);
                Assert.That(plans[1].HasPrefabOverrides, Is.False);
            }
            finally
            {
                if (containerInstance != null) Object.DestroyImmediate(containerInstance);
                if (containerSource != null) Object.DestroyImmediate(containerSource);
                if (reusableSource != null) Object.DestroyImmediate(reusableSource);
                AssetDatabase.DeleteAsset(testRoot);
            }
        }

        [Test]
        public void BatchPreflight_EstimatesEachReusableSourceOnceAndAppliesBudget()
        {
            const string testRoot = "Assets/VoxelBridgePreflightTestOutput";
            GameObject sourceObject = null;
            GameObject parent = null;
            VoxelStyleProfile profile = null;
            try
            {
                VoxelLodPipeline.EnsureAssetFolder(testRoot);
                sourceObject = GameObject.CreatePrimitive(PrimitiveType.Cube);
                GameObject prefab = PrefabUtility.SaveAsPrefabAsset(
                    sourceObject, testRoot + "/PreflightCube.prefab");
                Object.DestroyImmediate(sourceObject);
                sourceObject = null;

                parent = new GameObject("PreflightParent");
                PrefabUtility.InstantiatePrefab(prefab, parent.transform);
                PrefabUtility.InstantiatePrefab(prefab, parent.transform);
                profile = ScriptableObject.CreateInstance<VoxelStyleProfile>();
                var serializedProfile = new SerializedObject(profile);
                serializedProfile.FindProperty("baseVoxelSize").floatValue = 0.25f;
                SerializedProperty multipliers = serializedProfile.FindProperty("lodMultipliers");
                multipliers.arraySize = 2;
                multipliers.GetArrayElementAtIndex(0).intValue = 1;
                multipliers.GetArrayElementAtIndex(1).intValue = 2;
                serializedProfile.ApplyModifiedPropertiesWithoutUndo();

                var lodOptions = new VoxelLodBuildOptions
                {
                    ColorMode = VoxelColorMode.SingleColor,
                    ExportFolder = testRoot
                };
                var batchOptions = new VoxelLodBatchOptions
                {
                    MaximumEstimatedMemoryBytes = 1
                };
                VoxelLodBatchSourcePlan[] plans =
                    VoxelLodPipeline.GetAutomaticBatchPlans(parent, batchOptions);
                VoxelLodBatchPreflight preflight = VoxelLodBatchAnalyzer.Analyze(
                    plans, profile, lodOptions, batchOptions);

                Assert.That(preflight.CandidateCount, Is.EqualTo(2));
                Assert.That(preflight.UniqueConversionCount, Is.EqualTo(1));
                Assert.That(preflight.ReusedCount, Is.EqualTo(1));
                Assert.That(preflight.Sources[0].LodPlans, Has.Length.EqualTo(2));
                Assert.That(preflight.Sources[0].EstimatedPeakBytes, Is.GreaterThan(0));
                Assert.That(preflight.Sources[0].Risk,
                    Is.EqualTo(VoxelLodBatchMemoryRisk.OverBudget));
            }
            finally
            {
                if (parent != null) Object.DestroyImmediate(parent);
                if (sourceObject != null) Object.DestroyImmediate(sourceObject);
                if (profile != null) Object.DestroyImmediate(profile);
                AssetDatabase.DeleteAsset(testRoot);
            }
        }

        [Test]
        public void BatchRecovery_DeletesOnlyFamiliesMarkedAsIncomplete()
        {
            const string testRoot = "Assets/VoxelBridgeRecoveryTestOutput";
            const string markedFamily = testRoot + "/MarkedFamily";
            const string completeFamily = testRoot + "/CompleteFamily";
            try
            {
                VoxelLodPipeline.EnsureAssetFolder(markedFamily);
                VoxelLodPipeline.EnsureAssetFolder(completeFamily);
                VoxelLodBatchRecovery.MarkFamilyIncomplete(markedFamily, "InterruptedSource");

                string[] incomplete = VoxelLodBatchRecovery.FindIncompleteFamilies(testRoot);
                Assert.That(incomplete, Is.EqualTo(new[] { markedFamily }));

                int cleaned = VoxelLodBatchRecovery.CleanupIncompleteFamilies(testRoot);
                Assert.That(cleaned, Is.EqualTo(1));
                Assert.That(AssetDatabase.IsValidFolder(markedFamily), Is.False);
                Assert.That(AssetDatabase.IsValidFolder(completeFamily), Is.True);
            }
            finally
            {
                AssetDatabase.DeleteAsset(testRoot);
            }
        }

        [Test]
        public void AutomaticBatch_ResumesCompletedFamiliesFromCheckpoint()
        {
            if (!VoxelImporterIntegration.IsInstalled)
                Assert.Ignore("Voxel Importer es opcional y no está instalado.");

            const string testRoot = "Assets/VoxelBridgeCheckpointTestOutput";
            GameObject firstSource = null;
            GameObject secondSource = null;
            GameObject parent = null;
            VoxelStyleProfile profile = null;
            string checkpointSignature = null;
            try
            {
                VoxelLodPipeline.EnsureAssetFolder(testRoot);
                firstSource = GameObject.CreatePrimitive(PrimitiveType.Cube);
                firstSource.name = "CheckpointCube";
                GameObject firstPrefab = PrefabUtility.SaveAsPrefabAsset(
                    firstSource, testRoot + "/CheckpointCube.prefab");
                Object.DestroyImmediate(firstSource);
                firstSource = null;

                secondSource = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                secondSource.name = "CheckpointSphere";
                GameObject secondPrefab = PrefabUtility.SaveAsPrefabAsset(
                    secondSource, testRoot + "/CheckpointSphere.prefab");
                Object.DestroyImmediate(secondSource);
                secondSource = null;

                parent = new GameObject("CheckpointParent");
                PrefabUtility.InstantiatePrefab(firstPrefab, parent.transform);
                PrefabUtility.InstantiatePrefab(secondPrefab, parent.transform);
                profile = ScriptableObject.CreateInstance<VoxelStyleProfile>();
                var serializedProfile = new SerializedObject(profile);
                serializedProfile.FindProperty("baseVoxelSize").floatValue = 0.5f;
                serializedProfile.FindProperty("fillInterior").boolValue = false;
                serializedProfile.FindProperty("chunkCellSize").intValue = 16;
                SerializedProperty multipliers = serializedProfile.FindProperty("lodMultipliers");
                multipliers.arraySize = 1;
                multipliers.GetArrayElementAtIndex(0).intValue = 1;
                serializedProfile.ApplyModifiedPropertiesWithoutUndo();

                var lodOptions = new VoxelLodBuildOptions
                {
                    ColorMode = VoxelColorMode.SingleColor,
                    SingleColor = new Color32(80, 160, 220, 255),
                    ExportFolder = testRoot + "/Exports"
                };
                var batchOptions = new VoxelLodBatchOptions
                {
                    EnableCheckpoint = true,
                    ResumeInterruptedBatch = true,
                    CleanupInterval = 1
                };
                VoxelLodBatchSourcePlan[] plans =
                    VoxelLodPipeline.GetAutomaticBatchPlans(parent, batchOptions);
                checkpointSignature = VoxelLodBatchIdentity.CreateBatchSignature(
                    plans, profile, lodOptions, batchOptions);

                VoxelLodBatchBuildResult interrupted =
                    VoxelLodPipeline.GenerateAutomaticBatch(
                        parent, profile, lodOptions,
                        (_, message) => message.StartsWith("Modelo 2 de 2: preparando"),
                        batchOptions);
                Assert.That(interrupted.Cancelled, Is.True);
                Assert.That(interrupted.CreatedFamilyCount, Is.EqualTo(1));
                Assert.That(VoxelLodBatchCheckpointStore.Exists(checkpointSignature), Is.True);

                batchOptions.Preflight = interrupted.Preflight;
                VoxelLodBatchBuildResult resumed = VoxelLodPipeline.GenerateAutomaticBatch(
                    parent, profile, lodOptions, null, batchOptions);
                Assert.That(resumed.Cancelled, Is.False);
                Assert.That(resumed.SucceededCount, Is.EqualTo(2));
                Assert.That(resumed.ResumedCount, Is.EqualTo(1));
                Assert.That(resumed.CreatedFamilyCount, Is.EqualTo(1));
                Assert.That(VoxelLodBatchCheckpointStore.Exists(checkpointSignature), Is.False);
            }
            finally
            {
                if (!string.IsNullOrEmpty(checkpointSignature))
                    VoxelLodBatchCheckpointStore.Reset(checkpointSignature);
                if (parent != null) Object.DestroyImmediate(parent);
                if (firstSource != null) Object.DestroyImmediate(firstSource);
                if (secondSource != null) Object.DestroyImmediate(secondSource);
                if (profile != null) Object.DestroyImmediate(profile);
                AssetDatabase.DeleteAsset(testRoot);
            }
        }

        [Test]
        public void AutomaticLodPipeline_CreatesChunkedVoxFamilyAndLodPrefab()
        {
            if (!VoxelImporterIntegration.IsInstalled)
                Assert.Ignore("Voxel Importer es opcional y no está instalado.");

            const string testRoot = "Assets/VoxelBridgeTestOutput";
            GameObject root = null;
            GameObject prefabInstance = null;
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
                    ExportFolder = testRoot + "/Exports"
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
                Assert.That(prefab.name, Is.EqualTo("PipelineCube"));
                Assert.That(build.PrefabAssetPath, Does.EndWith("/PipelineCube.prefab"));
                string familyFolder = VoxelLodPipeline.NormalizeAssetPath(
                    Path.GetDirectoryName(build.ManifestAssetPath));
                Assert.That(VoxelLodPipeline.NormalizeAssetPath(
                    Path.GetDirectoryName(build.PrefabAssetPath)), Is.EqualTo(familyFolder));
                foreach (string voxPath in build.VoxAssetPaths)
                    Assert.That(VoxelLodPipeline.NormalizeAssetPath(Path.GetDirectoryName(voxPath)),
                        Is.EqualTo(familyFolder));
                Assert.That(VoxelLodPipeline.TryFindManifestForAsset(build.PrefabAssetPath,
                    out string foundFromPrefab, out VoxelLodSetManifest prefabManifest), Is.True);
                Assert.That(foundFromPrefab, Is.EqualTo(build.ManifestAssetPath));
                Assert.That(prefabManifest.familyId, Is.EqualTo(lod0.familyId));
                Assert.That(VoxelLodPipeline.TryFindManifestForAsset(build.VoxAssetPaths[0],
                    out string foundFromVox, out _), Is.True);
                Assert.That(foundFromVox, Is.EqualTo(build.ManifestAssetPath));
                prefabInstance = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
                Assert.That(prefabInstance, Is.Not.Null);
                Assert.That(prefabInstance.name, Is.EqualTo("PipelineCube"));
                GameObject lod0Object = prefabInstance.transform.GetChild(0).gameObject;
                Assert.That(VoxelBridgeWindow.TryResolveHierarchyLod(lod0Object,
                    out Object resolvedLodAsset, out string resolvedLodPath, out int resolvedLodIndex),
                    Is.True);
                Assert.That(resolvedLodAsset, Is.Not.Null);
                Assert.That(resolvedLodPath, Is.EqualTo(build.VoxAssetPaths[0]));
                Assert.That(resolvedLodIndex, Is.EqualTo(0));
                Assert.That(prefab.GetComponent<LODGroup>().lodCount, Is.EqualTo(2));
                Renderer[] chunkRenderers = prefab.transform.GetChild(0)
                    .GetComponentsInChildren<Renderer>(true);
                Assert.That(chunkRenderers.Length, Is.GreaterThan(1));
                Bounds renderedBounds = CalculateBounds(chunkRenderers);
                string chunkDiagnostics = string.Join(" | ", chunkRenderers.Select(renderer =>
                    $"{renderer.name}: pos={renderer.transform.position}, bounds={renderer.bounds}"));
                Assert.That(renderedBounds.size.x, Is.EqualTo(4f).Within(0.51f), chunkDiagnostics);
                Assert.That(renderedBounds.size.y, Is.EqualTo(2f).Within(0.51f));
                Assert.That(renderedBounds.size.z, Is.EqualTo(2f).Within(0.51f));
                Bounds coarseBounds = CalculateBounds(prefab.transform.GetChild(1)
                    .GetComponentsInChildren<Renderer>(true));
                float centerTolerance = profile.BaseVoxelSize * profile.GetLodMultiplier(1) + 1e-4f;
                Assert.That(Mathf.Abs(renderedBounds.center.x - coarseBounds.center.x),
                    Is.LessThanOrEqualTo(centerTolerance), chunkDiagnostics);
                Assert.That(Mathf.Abs(renderedBounds.center.y - coarseBounds.center.y),
                    Is.LessThanOrEqualTo(centerTolerance), chunkDiagnostics);
                Assert.That(Mathf.Abs(renderedBounds.center.z - coarseBounds.center.z),
                    Is.LessThanOrEqualTo(centerTolerance), chunkDiagnostics);

                string impostorFolder = familyFolder + "/Impostor";
                VoxelLodPipeline.EnsureAssetFolder(impostorFolder);
                string impostorAssetPath = impostorFolder + "/PipelineCube_Impostor.asset";
                var impostorData = ScriptableObject.CreateInstance<VoxelBridgeFakeImpostorAsset>();
                var impostorMesh = new Mesh { name = "PipelineCube_Impostor" };
                impostorMesh.vertices = new[]
                {
                    new Vector3(-2f, -1f, 0f), new Vector3(-2f, 1f, 0f),
                    new Vector3(2f, 1f, 0f), new Vector3(2f, -1f, 0f)
                };
                impostorMesh.triangles = new[] { 0, 1, 2, 0, 2, 3 };
                impostorMesh.RecalculateNormals();
                impostorMesh.RecalculateBounds();
                Shader shader = Shader.Find("HDRP/Lit") ?? Shader.Find("Hidden/InternalErrorShader");
                Assert.That(shader, Is.Not.Null);
                var impostorMaterial = new Material(shader) { name = "PipelineCube_Impostor" };
                impostorData.Mesh = impostorMesh;
                impostorData.Material = impostorMaterial;
                AssetDatabase.CreateAsset(impostorData, impostorAssetPath);
                AssetDatabase.AddObjectToAsset(impostorMesh, impostorData);
                AssetDatabase.AddObjectToAsset(impostorMaterial, impostorData);
                EditorUtility.SetDirty(impostorData);
                AssetDatabase.SaveAssets();

                Assert.That(VoxelLodPipeline.TryReadManifest(
                    build.ManifestAssetPath, out VoxelLodSetManifest manifestWithImpostor), Is.True);
                manifestWithImpostor.impostor = new VoxelImpostorEntry
                {
                    assetPath = impostorAssetPath,
                    quality = VoxelImpostorQuality.High,
                    amplifyVersion = AmplifyImpostorIntegration.SupportedVersion,
                    sourceLodIndex = 0,
                    cullScreenHeight = 0.01f,
                    crossFade = true,
                    fadeTransitionWidth = 0.15f
                };
                VoxelLodPipeline.SaveManifest(build.ManifestAssetPath, manifestWithImpostor);
                Assert.That(VoxelLodPipeline.TryReadManifest(
                    build.ManifestAssetPath, out VoxelLodSetManifest savedImpostorManifest), Is.True);
                Assert.That(savedImpostorManifest.impostor.quality,
                    Is.EqualTo(VoxelImpostorQuality.High));
                Assert.That(VoxelLodPipeline.RebuildPrefab(build.ManifestAssetPath),
                    Is.EqualTo(build.PrefabAssetPath));
                prefab = AssetDatabase.LoadAssetAtPath<GameObject>(build.PrefabAssetPath);
                LODGroup prefabLodGroup = prefab.GetComponent<LODGroup>();
                Assert.That(prefabLodGroup.lodCount, Is.EqualTo(3));
                Assert.That(prefabLodGroup.fadeMode, Is.EqualTo(LODFadeMode.CrossFade));
                LOD[] lodsWithImpostor = prefabLodGroup.GetLODs();
                Assert.That(lodsWithImpostor[1].screenRelativeTransitionHeight,
                    Is.EqualTo(0.3f).Within(1e-6f));
                Assert.That(lodsWithImpostor[2].screenRelativeTransitionHeight,
                    Is.EqualTo(0.01f).Within(1e-6f));
                Assert.That(lodsWithImpostor[2].renderers, Has.Length.EqualTo(1));
                Assert.That(lodsWithImpostor[2].renderers[0].gameObject.name, Is.EqualTo("Impostor"));
                Assert.That(VoxelLodPipeline.TryFindManifestForAsset(impostorAssetPath,
                    out string foundFromImpostor, out _), Is.True);
                Assert.That(foundFromImpostor, Is.EqualTo(build.ManifestAssetPath));

                string prefabGuid = AssetDatabase.AssetPathToGUID(build.PrefabAssetPath);
                string separatePrefabFolder = testRoot + "/SeparatePrefabs";
                VoxelLodPipeline.EnsureAssetFolder(separatePrefabFolder);
                string separatePrefabPath = separatePrefabFolder + "/PipelineCube_VoxelLOD.prefab";
                Assert.That(AssetDatabase.MoveAsset(build.PrefabAssetPath, separatePrefabPath), Is.Empty);
                string manifestAbsolute = VoxelLodPipeline.AssetPathToAbsolute(build.ManifestAssetPath);
                VoxelLodSetManifest looseManifest = JsonUtility.FromJson<VoxelLodSetManifest>(
                    File.ReadAllText(manifestAbsolute));
                looseManifest.prefabAssetPath = separatePrefabPath;
                File.WriteAllText(manifestAbsolute, JsonUtility.ToJson(looseManifest, true));
                AssetDatabase.ImportAsset(build.ManifestAssetPath, ImportAssetOptions.ForceSynchronousImport);
                string organizedPrefabPath = VoxelLodPipeline.RebuildPrefab(build.ManifestAssetPath);
                Assert.That(organizedPrefabPath, Is.Not.EqualTo(separatePrefabPath));
                Assert.That(VoxelLodPipeline.NormalizeAssetPath(Path.GetDirectoryName(organizedPrefabPath)),
                    Is.EqualTo(familyFolder));
                Assert.That(AssetDatabase.AssetPathToGUID(organizedPrefabPath), Is.EqualTo(prefabGuid));
                Assert.That(AssetDatabase.LoadMainAssetAtPath(separatePrefabPath), Is.Null);

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
                Assert.That(AssetDatabase.LoadAssetAtPath<GameObject>(organizedPrefabPath)
                    .GetComponent<LODGroup>().lodCount, Is.EqualTo(3));
            }
            finally
            {
                if (prefabInstance != null) Object.DestroyImmediate(prefabInstance);
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

        private static Bounds CalculateBounds(Renderer[] renderers)
        {
            Assert.That(renderers, Is.Not.Empty);
            Bounds bounds = renderers[0].bounds;
            for (int i = 1; i < renderers.Length; i++) bounds.Encapsulate(renderers[i].bounds);
            return bounds;
        }
    }
}
