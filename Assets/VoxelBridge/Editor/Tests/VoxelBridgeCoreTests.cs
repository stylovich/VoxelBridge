using System.IO;
using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

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
        public void StyleProfile_SelectsShadowlessLodsByModelSize()
        {
            VoxelStyleProfile profile = ScriptableObject.CreateInstance<VoxelStyleProfile>();
            try
            {
                Assert.That(profile.GetFirstShadowlessVoxelLodIndex(2f, 3), Is.EqualTo(-1));
                Assert.That(profile.GetFirstShadowlessVoxelLodIndex(0.75f, 3), Is.EqualTo(2));
                Assert.That(profile.GetFirstShadowlessVoxelLodIndex(0.25f, 3), Is.EqualTo(1));
                Assert.That(profile.GetFirstShadowlessVoxelLodIndex(0.25f, 1), Is.EqualTo(0));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void StyleProfile_SnapsWorldPositionToBaseVoxelGrid()
        {
            VoxelStyleProfile profile = ScriptableObject.CreateInstance<VoxelStyleProfile>();
            try
            {
                var serializedProfile = new SerializedObject(profile);
                serializedProfile.FindProperty("baseVoxelSize").floatValue = 0.024f;
                serializedProfile.ApplyModifiedPropertiesWithoutUndo();

                Vector3 source = new(1.001f, 0.013f, -0.013f);
                Vector3 snapped = profile.GetSnappedWorldPosition(source);
                Assert.That(snapped.x, Is.EqualTo(1.008f).Within(1e-6f));
                Assert.That(snapped.y, Is.EqualTo(0.024f).Within(1e-6f));
                Assert.That(snapped.z, Is.EqualTo(-0.024f).Within(1e-6f));

                serializedProfile.FindProperty("snapPlacedPivotsToVoxelGrid").boolValue = false;
                serializedProfile.ApplyModifiedPropertiesWithoutUndo();
                Assert.That(profile.GetSnappedWorldPosition(source), Is.EqualTo(source));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void StyleProfile_AdaptsLodTransitionsAroundReferenceSize()
        {
            VoxelStyleProfile profile = ScriptableObject.CreateInstance<VoxelStyleProfile>();
            try
            {
                var serializedProfile = new SerializedObject(profile);
                serializedProfile.FindProperty("lodTransitionMode").enumValueIndex =
                    (int)VoxelLodTransitionMode.AdaptiveByModelSize;
                serializedProfile.FindProperty("lodReferenceModelSize").floatValue = 4f;
                serializedProfile.FindProperty("lodSizeAdaptationStrength").floatValue = 0.5f;
                serializedProfile.FindProperty("lodMinimumTransitionScale").floatValue = 0.35f;
                serializedProfile.FindProperty("lodMaximumTransitionScale").floatValue = 2f;
                SerializedProperty transitions =
                    serializedProfile.FindProperty("lodScreenHeights");
                transitions.arraySize = 3;
                transitions.GetArrayElementAtIndex(0).floatValue = 0.3f;
                transitions.GetArrayElementAtIndex(1).floatValue = 0.18f;
                transitions.GetArrayElementAtIndex(2).floatValue = 0.1f;
                serializedProfile.ApplyModifiedPropertiesWithoutUndo();

                Assert.That(profile.GetLodScreenHeight(0, 4f),
                    Is.EqualTo(0.3f).Within(1e-6f));
                Assert.That(profile.GetLodScreenHeight(1, 0.5f),
                    Is.EqualTo(0.18f * Mathf.Sqrt(0.5f / 4f)).Within(1e-6f));
                Assert.That(profile.GetLodScreenHeight(2, 40f),
                    Is.EqualTo(0.2f).Within(1e-6f));
                Assert.That(profile.GetMinimumLodScreenHeight(2),
                    Is.EqualTo(0.035f).Within(1e-6f));
                Assert.That(profile.TryValidate(out string error), Is.True, error);
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

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
                Assert.That(high.CullScreenHeight, Is.LessThan(medium.CullScreenHeight));
                Assert.That(architecture.ImpostorType,
                    Is.EqualTo(VoxelImpostorType.HemiOctahedron));
                Assert.That(architecture.MaxVertices, Is.EqualTo(10));
                Assert.That(architecture.CullScreenHeight, Is.EqualTo(0.0005f).Within(1e-7f));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void ImpostorLodRanges_ExtendLaterVoxelLevelsWithoutMovingLod0()
        {
            var voxelLods = new[]
            {
                new LOD(0.37f, System.Array.Empty<Renderer>()),
                new LOD(0.22f, System.Array.Empty<Renderer>()),
                new LOD(0.12f, System.Array.Empty<Renderer>())
            };

            AmplifyImpostorIntegration.ExpandVoxelLodRangesTowardImpostor(
                voxelLods, 0.01f);

            Assert.That(voxelLods[0].screenRelativeTransitionHeight,
                Is.EqualTo(0.37f).Within(1e-6f));
            Assert.That(voxelLods[1].screenRelativeTransitionHeight,
                Is.EqualTo(0.17f).Within(1e-6f));
            Assert.That(voxelLods[2].screenRelativeTransitionHeight,
                Is.EqualTo(0.12f + (0.01f - 0.12f) / 3f).Within(1e-6f));
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
        public void ImpostorProfile_SelectsAutomaticQualityAtConfiguredBoundaries()
        {
            VoxelImpostorProfile profile = ScriptableObject.CreateInstance<VoxelImpostorProfile>();
            try
            {
                Assert.That(profile.TrySelectAutomaticQuality(
                    0.499f, out VoxelImpostorQuality belowMinimum), Is.False);
                Assert.That(belowMinimum, Is.EqualTo(VoxelImpostorQuality.Unspecified));
                Assert.That(profile.TrySelectAutomaticQuality(
                    0.5f, out VoxelImpostorQuality low), Is.True);
                Assert.That(low, Is.EqualTo(VoxelImpostorQuality.Low));
                Assert.That(profile.TrySelectAutomaticQuality(
                    4f, out VoxelImpostorQuality medium), Is.True);
                Assert.That(medium, Is.EqualTo(VoxelImpostorQuality.Medium));
                Assert.That(profile.TrySelectAutomaticQuality(
                    24f, out VoxelImpostorQuality architecture), Is.True);
                Assert.That(architecture, Is.EqualTo(VoxelImpostorQuality.Architecture));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void ImpostorBatchPlan_PrioritizesLargeFamiliesAndHonorsAtlasBudget()
        {
            const string testRoot = "Assets/VoxelBridgeImpostorPlanTestOutput";
            VoxelImpostorProfile profile = ScriptableObject.CreateInstance<VoxelImpostorProfile>();
            try
            {
                VoxelLodPipeline.EnsureAssetFolder(testRoot);
                string tiny = SaveImpostorPlanManifest(testRoot, "Tiny", 0.25f);
                string prop = SaveImpostorPlanManifest(testRoot, "Prop", 2f);
                string vehicle = SaveImpostorPlanManifest(testRoot, "Vehicle", 8f);
                string building = SaveImpostorPlanManifest(testRoot, "Building", 30f);
                string excluded = SaveImpostorPlanManifest(
                    testRoot, "Excluded", 30f, impostorDisabled: true);
                var options = new VoxelImpostorBatchOptions
                {
                    SelectQualityBySize = true,
                    MaximumEstimatedAtlasBytes = 34L * 1024L * 1024L
                };

                VoxelImpostorBatchPlan plan = AmplifyImpostorIntegration.CreateBatchPlan(
                    new[] { tiny, prop, vehicle, building, vehicle, excluded }, profile, options);

                Assert.That(plan.CandidateCount, Is.EqualTo(5));
                Assert.That(plan.Entries.Length, Is.EqualTo(2));
                Assert.That(plan.EstimatedAtlasBytes,
                    Is.LessThanOrEqualTo(options.MaximumEstimatedAtlasBytes));
                Assert.That(plan.Entries.Single(entry =>
                        entry.ManifestAssetPath == building).Quality,
                    Is.EqualTo(VoxelImpostorQuality.Medium));
                Assert.That(plan.Entries.Single(entry =>
                        entry.ManifestAssetPath == vehicle).Quality,
                    Is.EqualTo(VoxelImpostorQuality.Low));
                Assert.That(plan.Skips.Single(skip => skip.ManifestAssetPath == tiny).Reason,
                    Is.EqualTo(VoxelImpostorBatchSkipReason.BelowMinimumSize));
                Assert.That(plan.Skips.Single(skip => skip.ManifestAssetPath == prop).Reason,
                    Is.EqualTo(VoxelImpostorBatchSkipReason.AtlasBudget));
                Assert.That(plan.Skips.Single(skip => skip.ManifestAssetPath == excluded).Reason,
                    Is.EqualTo(VoxelImpostorBatchSkipReason.DisabledByUser));
                Assert.That(AmplifyImpostorIntegration.FindPendingManifestAssetPaths(testRoot),
                    Does.Not.Contain(excluded));
                Assert.That(AmplifyImpostorIntegration.EstimateAtlasBytes(
                        profile, VoxelImpostorQuality.Low),
                    Is.EqualTo(6_990_506L));
                Assert.That(AmplifyImpostorIntegration.EstimateAtlasBytes(
                        profile, VoxelImpostorQuality.Medium),
                    Is.EqualTo(27_962_026L));
            }
            finally
            {
                Object.DestroyImmediate(profile);
                AssetDatabase.DeleteAsset(testRoot);
            }
        }

        [Test]
        public void ImpostorBatch_DeduplicatesFamiliesContinuesAfterFailureAndCanCancel()
        {
            VoxelImpostorProfile profile = ScriptableObject.CreateInstance<VoxelImpostorProfile>();
            try
            {
                int calls = 0;
                VoxelImpostorBatchBuildResult result =
                    AmplifyImpostorIntegration.GenerateForManifests(
                        new[]
                        {
                            "Assets/A.voxset.json",
                            "Assets/A.voxset.json",
                            "Assets/B.voxset.json"
                        },
                        profile, VoxelImpostorQuality.Medium, null,
                        (path, unusedProfile, unusedQuality) =>
                        {
                            calls++;
                            if (path.EndsWith("B.voxset.json"))
                                throw new System.InvalidOperationException("Fallo controlado");
                            return new VoxelImpostorBuildResult(
                                path + ".asset", path + ".prefab");
                        });

                Assert.That(calls, Is.EqualTo(2));
                Assert.That(result.CandidateCount, Is.EqualTo(2));
                Assert.That(result.GeneratedCount, Is.EqualTo(1));
                Assert.That(result.FailedCount, Is.EqualTo(1));
                Assert.That(result.Failures[0].ManifestAssetPath,
                    Is.EqualTo("Assets/B.voxset.json"));
                Assert.That(result.Cancelled, Is.False);

                VoxelImpostorBatchBuildResult cancelled =
                    AmplifyImpostorIntegration.GenerateForManifests(
                        new[] { "Assets/C.voxset.json" }, profile,
                        VoxelImpostorQuality.Low, (progress, message) => true,
                        (path, unusedProfile, unusedQuality) => throw new AssertionException(
                            "El generador no debe ejecutarse después de cancelar."));
                Assert.That(cancelled.Cancelled, Is.True);
                Assert.That(cancelled.GeneratedCount, Is.Zero);
                Assert.That(cancelled.RemainingCount, Is.EqualTo(1));
            }
            finally
            {
                Object.DestroyImmediate(profile);
            }
        }

        [Test]
        public void ImpostorBatch_BakesAmplifyAssetAndAppendsFinalLod()
        {
            if (!VoxelImporterIntegration.IsInstalled)
                Assert.Ignore("Voxel Importer es opcional y no está instalado.");
            AmplifyImpostorCompatibility compatibility =
                AmplifyImpostorIntegration.GetCompatibility();
            if (!compatibility.CanBake)
                Assert.Ignore(compatibility.Message);

            const string testRoot = "Assets/VoxelBridgeAmplifyBatchTestOutput";
            GameObject source = null;
            VoxelStyleProfile voxelProfile = null;
            VoxelImpostorProfile impostorProfile = null;
            try
            {
                source = GameObject.CreatePrimitive(PrimitiveType.Cube);
                source.name = "AmplifyBatchCube";
                voxelProfile = ScriptableObject.CreateInstance<VoxelStyleProfile>();
                var serializedProfile = new SerializedObject(voxelProfile);
                serializedProfile.FindProperty("baseVoxelSize").floatValue = 0.5f;
                serializedProfile.FindProperty("chunkCellSize").intValue = 16;
                SerializedProperty multipliers =
                    serializedProfile.FindProperty("lodMultipliers");
                multipliers.arraySize = 1;
                multipliers.GetArrayElementAtIndex(0).intValue = 1;
                SerializedProperty transitions =
                    serializedProfile.FindProperty("lodScreenHeights");
                transitions.arraySize = 1;
                transitions.GetArrayElementAtIndex(0).floatValue = 0.05f;
                serializedProfile.ApplyModifiedPropertiesWithoutUndo();
                impostorProfile = ScriptableObject.CreateInstance<VoxelImpostorProfile>();

                VoxelLodBuildResult build = VoxelLodPipeline.GenerateAutomatic(
                    source, voxelProfile, new VoxelLodBuildOptions
                    {
                        ColorMode = VoxelColorMode.SingleColor,
                        SingleColor = new Color32(120, 160, 200, 255),
                        ExportFolder = testRoot
                    });
                Assert.That(VoxelLodPipeline.TryReadManifest(
                    build.ManifestAssetPath, out VoxelLodSetManifest disabledManifest), Is.True);
                disabledManifest.impostorDisabled = true;
                VoxelLodPipeline.SaveManifest(build.ManifestAssetPath, disabledManifest);
                VoxelImpostorBatchBuildResult batch =
                    AmplifyImpostorIntegration.GenerateForManifests(
                        new[] { build.ManifestAssetPath }, impostorProfile,
                        VoxelImpostorQuality.Low);

                Assert.That(batch.Cancelled, Is.False);
                Assert.That(batch.GeneratedCount, Is.EqualTo(1));
                Assert.That(batch.FailedCount, Is.Zero);
                Assert.That(AssetDatabase.LoadMainAssetAtPath(
                    batch.Builds[0].ImpostorAssetPath), Is.Not.Null);
                string impostorFolder = VoxelLodPipeline.NormalizeAssetPath(
                    Path.GetDirectoryName(batch.Builds[0].ImpostorAssetPath));
                string[] texturePaths = AssetDatabase.FindAssets(
                        "t:Texture2D", new[] { impostorFolder })
                    .Select(AssetDatabase.GUIDToAssetPath)
                    .ToArray();
                Assert.That(texturePaths.Length, Is.EqualTo(5));
                foreach (string texturePath in texturePaths)
                {
                    var importer = AssetImporter.GetAtPath(texturePath) as TextureImporter;
                    Assert.That(importer, Is.Not.Null, texturePath);
                    Assert.That(importer.mipmapEnabled, Is.True, texturePath);
                    Assert.That(importer.streamingMipmaps, Is.True, texturePath);
                }
                Assert.That(VoxelLodPipeline.TryReadManifest(
                    build.ManifestAssetPath, out VoxelLodSetManifest manifest), Is.True);
                Assert.That(manifest.impostor, Is.Not.Null);
                Assert.That(manifest.impostorDisabled, Is.False);
                Assert.That(manifest.impostor.quality, Is.EqualTo(VoxelImpostorQuality.Low));
                GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(
                    batch.Builds[0].PrefabAssetPath);
                Assert.That(prefab, Is.Not.Null);
                Assert.That(prefab.GetComponent<LODGroup>().lodCount, Is.EqualTo(2));
                Assert.That(AmplifyImpostorIntegration.FindPendingManifestAssetPaths(testRoot),
                    Does.Not.Contain(build.ManifestAssetPath));
            }
            finally
            {
                if (source != null) Object.DestroyImmediate(source);
                if (voxelProfile != null) Object.DestroyImmediate(voxelProfile);
                if (impostorProfile != null) Object.DestroyImmediate(impostorProfile);
                AssetDatabase.DeleteAsset(testRoot);
            }
        }

        [Test]
        public void IndividualGeneration_BakesImpostorAndUpdatesPrefab()
        {
            if (!VoxelImporterIntegration.IsInstalled)
                Assert.Ignore("Voxel Importer es opcional y no está instalado.");
            AmplifyImpostorCompatibility compatibility =
                AmplifyImpostorIntegration.GetCompatibility();
            if (!compatibility.CanBake)
                Assert.Ignore(compatibility.Message);

            const string testRoot = "Assets/VoxelBridgeIndividualImpostorTestOutput";
            GameObject source = null;
            VoxelStyleProfile voxelProfile = null;
            VoxelImpostorProfile impostorProfile = null;
            VoxelBridgeWindow window = null;
            Object previousSelection = Selection.activeObject;
            UnityEngine.SceneManagement.Scene testScene = default;
            try
            {
                testScene = UnityEditor.SceneManagement.EditorSceneManager.NewPreviewScene();
                source = GameObject.CreatePrimitive(PrimitiveType.Cube);
                source.name = "IndividualImpostorCube";
                UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(source, testScene);

                voxelProfile = ScriptableObject.CreateInstance<VoxelStyleProfile>();
                var serializedProfile = new SerializedObject(voxelProfile);
                serializedProfile.FindProperty("baseVoxelSize").floatValue = 0.5f;
                serializedProfile.FindProperty("chunkCellSize").intValue = 16;
                SerializedProperty multipliers =
                    serializedProfile.FindProperty("lodMultipliers");
                multipliers.arraySize = 1;
                multipliers.GetArrayElementAtIndex(0).intValue = 1;
                SerializedProperty transitions =
                    serializedProfile.FindProperty("lodScreenHeights");
                transitions.arraySize = 1;
                transitions.GetArrayElementAtIndex(0).floatValue = 0.05f;
                serializedProfile.ApplyModifiedPropertiesWithoutUndo();
                impostorProfile = ScriptableObject.CreateInstance<VoxelImpostorProfile>();

                window = ScriptableObject.CreateInstance<VoxelBridgeWindow>();
                SetWindowField(window, "source", source);
                SetWindowField(window, "styleProfile", voxelProfile);
                SetWindowField(window, "impostorProfile", impostorProfile);
                SetWindowField(window, "impostorQuality", VoxelImpostorQuality.Low);
                SetWindowField(window, "colorMode", VoxelColorMode.SingleColor);
                SetWindowField(window, "exportFolder", testRoot);
                SetWindowField(window, "individualGenerateImpostor", true);
                SetWindowField(window, "individualPlaceInScene", false);

                MethodInfo generate = typeof(VoxelBridgeWindow).GetMethod(
                    "GenerateAutomaticLods", BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That(generate, Is.Not.Null);
                generate.Invoke(window, null);

                Assert.That(GetWindowField<Object>(window, "lastImpostorAsset"), Is.Not.Null,
                    GetWindowField<string>(window, "status"));
                GameObject generatedPrefab =
                    GetWindowField<Object>(window, "lastPrefabAsset") as GameObject;
                Assert.That(generatedPrefab, Is.Not.Null);
                LODGroup lodGroup = generatedPrefab.GetComponent<LODGroup>();
                Assert.That(lodGroup, Is.Not.Null);
                Assert.That(lodGroup.lodCount, Is.EqualTo(2));
            }
            finally
            {
                Selection.activeObject = previousSelection;
                if (source != null) Object.DestroyImmediate(source);
                if (window != null) Object.DestroyImmediate(window);
                if (voxelProfile != null) Object.DestroyImmediate(voxelProfile);
                if (impostorProfile != null) Object.DestroyImmediate(impostorProfile);
                AssetDatabase.DeleteAsset(testRoot);
                if (testScene.IsValid() && testScene.isLoaded)
                    UnityEditor.SceneManagement.EditorSceneManager.ClosePreviewScene(testScene);
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
        public void AutomaticBatchSources_IgnoresInactiveObjectsByDefaultAndCanIncludeThem()
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

                GameObject inactiveNestedCube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                inactiveNestedCube.name = "InactiveNestedGeometry";
                inactiveNestedCube.transform.SetParent(nestedFamily.transform, false);
                inactiveNestedCube.transform.localPosition = Vector3.right * 100f;
                inactiveNestedCube.SetActive(false);

                var emptyFamily = new GameObject("EmptyFamily");
                emptyFamily.transform.SetParent(parent.transform, false);

                GameObject[] sources = VoxelLodPipeline.GetAutomaticBatchSources(parent);
                GameObject[] sourcesIncludingInactive = VoxelLodPipeline.GetAutomaticBatchSources(
                    parent, new VoxelLodBatchOptions { IgnoreInactiveObjects = false });
                Bounds activeBounds = MeshVoxelizer.GetSourceBounds(
                    nestedFamily, includeInactiveObjects: false);
                Bounds allBounds = MeshVoxelizer.GetSourceBounds(
                    nestedFamily, includeInactiveObjects: true);

                Assert.That(sources, Is.EqualTo(new[] { nestedFamily }));
                Assert.That(sourcesIncludingInactive,
                    Is.EqualTo(new[] { nestedFamily, inactiveFamily }));
                Assert.That(sources.Contains(nestedCube), Is.False);
                Assert.That(sources.Contains(emptyFamily), Is.False);
                Assert.That(activeBounds.size.x, Is.LessThan(2f));
                Assert.That(allBounds.size.x, Is.GreaterThan(50f));
            }
            finally
            {
                Object.DestroyImmediate(parent);
            }
        }

        [Test]
        public void SinglePlacement_CopiesSceneStateDisablesOriginalAndSupportsUndo()
        {
            const string testRoot = "Assets/VoxelBridgeSinglePlacementTestOutput";
            GameObject parent = null;
            GameObject sourceObject = null;
            GameObject prefabSource = null;
            GameObject placedInstance = null;
            VoxelStyleProfile profile = null;
            UnityEngine.SceneManagement.Scene originalScene =
                UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            UnityEngine.SceneManagement.Scene testScene = default;
            try
            {
                UnityEditor.SceneManagement.NewSceneMode sceneMode =
                    originalScene.IsValid() && originalScene.isLoaded &&
                    !string.IsNullOrEmpty(originalScene.path)
                        ? UnityEditor.SceneManagement.NewSceneMode.Additive
                        : UnityEditor.SceneManagement.NewSceneMode.Single;
                testScene = UnityEditor.SceneManagement.EditorSceneManager.NewScene(
                    UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,
                    sceneMode);
                VoxelLodPipeline.EnsureAssetFolder(testRoot);
                prefabSource = GameObject.CreatePrimitive(PrimitiveType.Cube);
                prefabSource.name = "ConvertedSingle";
                string prefabPath = testRoot + "/ConvertedSingle.prefab";
                GameObject prefab = PrefabUtility.SaveAsPrefabAsset(prefabSource, prefabPath);
                Object.DestroyImmediate(prefabSource);
                prefabSource = null;
                Assert.That(prefab, Is.Not.Null);

                parent = new GameObject("SinglePlacementParent");
                sourceObject = new GameObject("SingleSource");
                sourceObject.transform.SetParent(parent.transform, false);
                sourceObject.transform.localPosition = new Vector3(2.24f, 3.24f, -4.24f);
                sourceObject.transform.localRotation = Quaternion.Euler(10f, 20f, 30f);
                sourceObject.transform.localScale = new Vector3(1.5f, 0.75f, 2f);
                sourceObject.layer = 6;
                GameObjectUtility.SetStaticEditorFlags(
                    sourceObject, StaticEditorFlags.OccluderStatic);

                var build = new VoxelLodBuildResult(
                    null, prefabPath, System.Array.Empty<string>());
                profile = ScriptableObject.CreateInstance<VoxelStyleProfile>();
                var serializedProfile = new SerializedObject(profile);
                serializedProfile.FindProperty("baseVoxelSize").floatValue = 0.5f;
                serializedProfile.ApplyModifiedPropertiesWithoutUndo();
                Assert.That(VoxelLodBatchScenePlacement.CanPlace(sourceObject), Is.True);
                Assert.That(VoxelLodBatchScenePlacement.CanPlace(prefab), Is.False);

                VoxelLodSinglePlacementResult placement =
                    VoxelLodBatchScenePlacement.PlaceSingle(sourceObject, build, profile, false);
                placedInstance = placement.Instance;
                Assert.That(placement.OriginalObjectDisabled, Is.False);
                Assert.That(sourceObject.activeSelf, Is.True);
                Assert.That(placedInstance.activeSelf, Is.True);
                Assert.That(placedInstance.name, Is.EqualTo("SingleSource_Voxel"));
                Assert.That(placedInstance.transform.parent, Is.EqualTo(parent.transform));
                Assert.That(placedInstance.transform.GetSiblingIndex(),
                    Is.EqualTo(sourceObject.transform.GetSiblingIndex() + 1));
                Assert.That(placedInstance.transform.position,
                    Is.EqualTo(new Vector3(2f, 3f, -4f)));
                Assert.That(Quaternion.Angle(placedInstance.transform.localRotation,
                    sourceObject.transform.localRotation), Is.LessThan(1e-4f));
                Assert.That(placedInstance.transform.localScale,
                    Is.EqualTo(sourceObject.transform.localScale));
                Assert.That(placedInstance.layer, Is.EqualTo(sourceObject.layer));
                Assert.That(GameObjectUtility.GetStaticEditorFlags(placedInstance),
                    Is.EqualTo(GameObjectUtility.GetStaticEditorFlags(sourceObject)));

                Undo.PerformUndo();
                Assert.That(placedInstance == null, Is.True);
                Assert.That(sourceObject.activeSelf, Is.True);
                placedInstance = null;

                placement = VoxelLodBatchScenePlacement.PlaceSingle(
                    sourceObject, build, profile, true);
                placedInstance = placement.Instance;
                Assert.That(placement.OriginalObjectDisabled, Is.True);
                Assert.That(sourceObject.activeSelf, Is.False);
                Assert.That(placedInstance.activeSelf, Is.True);

                Undo.PerformUndo();
                Assert.That(placedInstance == null, Is.True);
                Assert.That(sourceObject.activeSelf, Is.True);
                placedInstance = null;
            }
            finally
            {
                if (placedInstance != null) Object.DestroyImmediate(placedInstance);
                if (sourceObject != null) Object.DestroyImmediate(sourceObject);
                if (parent != null) Object.DestroyImmediate(parent);
                if (prefabSource != null) Object.DestroyImmediate(prefabSource);
                if (profile != null) Object.DestroyImmediate(profile);
                AssetDatabase.DeleteAsset(testRoot);
                if (originalScene.IsValid() && originalScene.isLoaded)
                    UnityEngine.SceneManagement.SceneManager.SetActiveScene(originalScene);
                if (testScene.IsValid() && testScene.isLoaded &&
                    UnityEngine.SceneManagement.SceneManager.sceneCount > 1)
                    UnityEditor.SceneManagement.EditorSceneManager.CloseScene(testScene, true);
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
                    VoxelLodBatchScenePlacement.Place(parent, batch, profile, true);
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
                    VoxelLodBatchScenePlacement.Place(parent, completeBatch, profile, true);
                placedRoot = skippedChildPlacement.Root;
                Assert.That(skippedChildPlacement.OriginalRootDisabled, Is.False);
                Assert.That(parent.activeSelf, Is.True);
                Assert.That(placedRoot.activeSelf, Is.False);
                Undo.PerformUndo();
                Assert.That(placedRoot == null, Is.True);
                placedRoot = null;
                Object.DestroyImmediate(nonVisualChild);

                VoxelLodBatchPlacementResult placement =
                    VoxelLodBatchScenePlacement.Place(parent, completeBatch, profile, true);
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
        public void AutomaticBatch_PreservesPrefabVariantAsReusableSource()
        {
            const string testRoot = "Assets/VoxelBridgeVariantBatchTestOutput";
            GameObject baseSource = null;
            GameObject variantSource = null;
            GameObject parent = null;
            try
            {
                VoxelLodPipeline.EnsureAssetFolder(testRoot);
                baseSource = GameObject.CreatePrimitive(PrimitiveType.Cube);
                baseSource.name = "VariantBase";
                GameObject basePrefab = PrefabUtility.SaveAsPrefabAsset(
                    baseSource, testRoot + "/VariantBase.prefab");
                Object.DestroyImmediate(baseSource);
                baseSource = null;

                variantSource = PrefabUtility.InstantiatePrefab(basePrefab) as GameObject;
                Assert.That(variantSource, Is.Not.Null);
                variantSource.transform.localScale = new Vector3(1f, 2f, 1f);
                GameObject variantPrefab = PrefabUtility.SaveAsPrefabAsset(
                    variantSource, testRoot + "/TallVariant.prefab");
                Object.DestroyImmediate(variantSource);
                variantSource = null;
                Assert.That(PrefabUtility.GetPrefabAssetType(variantPrefab),
                    Is.EqualTo(PrefabAssetType.Variant));

                parent = new GameObject("VariantParent");
                PrefabUtility.InstantiatePrefab(variantPrefab, parent.transform);
                PrefabUtility.InstantiatePrefab(variantPrefab, parent.transform);
                VoxelLodBatchSourcePlan[] plans = VoxelLodPipeline.GetAutomaticBatchPlans(
                    parent, new VoxelLodBatchOptions());

                Assert.That(plans, Has.Length.EqualTo(2));
                Assert.That(plans[0].ConversionSource, Is.EqualTo(variantPrefab));
                Assert.That(plans[1].ConversionSource, Is.EqualTo(variantPrefab));
                Assert.That(plans[0].ReuseKey, Is.EqualTo(plans[1].ReuseKey));
            }
            finally
            {
                if (parent != null) Object.DestroyImmediate(parent);
                if (variantSource != null) Object.DestroyImmediate(variantSource);
                if (baseSource != null) Object.DestroyImmediate(baseSource);
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
                Assert.That(preflight.Sources[0].InitialLodIndex, Is.EqualTo(1));
                Assert.That(preflight.Sources[0].InitialVoxelMultiplier, Is.EqualTo(2));
                Assert.That(preflight.AdaptedCount, Is.EqualTo(1));
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
        public void BatchPreflight_UsesSmallestAllowedLodBaseThatFitsBudget()
        {
            GameObject parent = null;
            VoxelStyleProfile profile = null;
            try
            {
                parent = new GameObject("AdaptivePreflightParent");
                GameObject cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                cube.transform.SetParent(parent.transform, false);
                profile = ScriptableObject.CreateInstance<VoxelStyleProfile>();
                var serializedProfile = new SerializedObject(profile);
                serializedProfile.FindProperty("baseVoxelSize").floatValue = 0.05f;
                SerializedProperty multipliers = serializedProfile.FindProperty("lodMultipliers");
                multipliers.arraySize = 3;
                multipliers.GetArrayElementAtIndex(0).intValue = 1;
                multipliers.GetArrayElementAtIndex(1).intValue = 2;
                multipliers.GetArrayElementAtIndex(2).intValue = 4;
                serializedProfile.ApplyModifiedPropertiesWithoutUndo();

                var lodOptions = new VoxelLodBuildOptions
                {
                    ColorMode = VoxelColorMode.SingleColor,
                    ExportFolder = "Assets"
                };
                VoxelLodBatchSourcePlan[] plans = VoxelLodPipeline.GetAutomaticBatchPlans(
                    parent, new VoxelLodBatchOptions());
                var baselineOptions = new VoxelLodBatchOptions
                {
                    AdaptInitialVoxelSize = false,
                    MaximumEstimatedMemoryBytes = long.MaxValue
                };
                long finePeak = VoxelLodBatchAnalyzer.Analyze(
                    plans, profile, lodOptions, baselineOptions).Sources[0].EstimatedPeakBytes;

                serializedProfile.FindProperty("baseVoxelSize").floatValue = 0.1f;
                serializedProfile.ApplyModifiedPropertiesWithoutUndo();
                long coarsePeak = VoxelLodBatchAnalyzer.Analyze(
                    plans, profile, lodOptions, baselineOptions).Sources[0].EstimatedPeakBytes;
                serializedProfile.FindProperty("baseVoxelSize").floatValue = 0.05f;
                serializedProfile.ApplyModifiedPropertiesWithoutUndo();
                Assert.That(finePeak, Is.GreaterThan(coarsePeak));

                var adaptiveOptions = new VoxelLodBatchOptions
                {
                    AdaptInitialVoxelSize = true,
                    MaximumInitialLodIndex = 2,
                    MaximumEstimatedMemoryBytes = coarsePeak + (finePeak - coarsePeak) / 2
                };
                VoxelLodBatchPreflight adaptive = VoxelLodBatchAnalyzer.Analyze(
                    plans, profile, lodOptions, adaptiveOptions);

                Assert.That(adaptive.Sources[0].IsOverBudget, Is.False);
                Assert.That(adaptive.Sources[0].InitialLodIndex, Is.EqualTo(1));
                Assert.That(adaptive.Sources[0].InitialVoxelMultiplier, Is.EqualTo(2));
                Assert.That(adaptive.Sources[0].LodPlans[0].VoxelSize,
                    Is.EqualTo(0.1f).Within(1e-6f));
            }
            finally
            {
                if (parent != null) Object.DestroyImmediate(parent);
                if (profile != null) Object.DestroyImmediate(profile);
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
                serializedProfile.FindProperty("lodTransitionMode").enumValueIndex =
                    (int)VoxelLodTransitionMode.AdaptiveByModelSize;
                serializedProfile.FindProperty("lastLodShadowSizeThreshold").floatValue = 100f;
                serializedProfile.FindProperty("penultimateLodShadowSizeThreshold").floatValue = 0.01f;
                SerializedProperty multipliers = serializedProfile.FindProperty("lodMultipliers");
                multipliers.arraySize = 2;
                multipliers.GetArrayElementAtIndex(0).intValue = 1;
                multipliers.GetArrayElementAtIndex(1).intValue = 2;
                serializedProfile.ApplyModifiedPropertiesWithoutUndo();
                VoxelLodPipeline.EnsureAssetFolder(testRoot);
                AssetDatabase.CreateAsset(profile, testRoot + "/VoxelStyleProfile.asset");

                var options = new VoxelLodBuildOptions
                {
                    ColorMode = VoxelColorMode.SingleColor,
                    SingleColor = new Color32(180, 120, 60, 255),
                    AlphaCutoff = 0.1f,
                    ExportFolder = testRoot + "/Exports"
                };

                VoxelLodBuildResult build = VoxelLodPipeline.GenerateAutomatic(root, profile, options);

                Assert.That(build.VoxAssetPaths.Length, Is.EqualTo(2));
                Assert.That(AmplifyImpostorIntegration.FindPendingManifestAssetPaths(testRoot),
                    Does.Contain(build.ManifestAssetPath));
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
                Assert.That(VoxelLodPipeline.TryReadManifest(
                    build.ManifestAssetPath, out VoxelLodSetManifest adaptiveManifest), Is.True);
                Assert.That(adaptiveManifest.formatVersion, Is.EqualTo(4));
                Assert.That(adaptiveManifest.lodGroupSize, Is.GreaterThan(0f));
                Assert.That(profile.GetFirstShadowlessVoxelLodIndex(
                    adaptiveManifest.lodGroupSize, 2), Is.EqualTo(1));
                LOD[] adaptiveLods = prefab.GetComponent<LODGroup>().GetLODs();
                Assert.That(adaptiveLods[0].renderers.All(renderer =>
                    renderer.shadowCastingMode == ShadowCastingMode.On), Is.True);
                Assert.That(adaptiveLods[1].renderers.All(renderer =>
                    renderer.shadowCastingMode == ShadowCastingMode.Off), Is.True,
                    string.Join(", ", adaptiveLods[1].renderers.Select(renderer =>
                        renderer.shadowCastingMode.ToString())));
                Assert.That(adaptiveLods[0].screenRelativeTransitionHeight,
                    Is.EqualTo(profile.GetLodScreenHeight(0, adaptiveManifest.lodGroupSize))
                        .Within(1e-6f));
                Assert.That(adaptiveManifest.lods[1].screenRelativeTransitionHeight,
                    Is.EqualTo(adaptiveLods[1].screenRelativeTransitionHeight).Within(1e-6f));
                Assert.That(AmplifyImpostorIntegration.TryGetLastVoxelTransition(
                    adaptiveManifest, out float persistedTransition), Is.True);
                Assert.That(persistedTransition,
                    Is.EqualTo(adaptiveLods[1].screenRelativeTransitionHeight).Within(1e-6f));
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
                    cullScreenHeight = 0.01f
                };
                VoxelLodPipeline.SaveManifest(build.ManifestAssetPath, manifestWithImpostor);
                Assert.That(VoxelLodPipeline.TryReadManifest(
                    build.ManifestAssetPath, out VoxelLodSetManifest savedImpostorManifest), Is.True);
                Assert.That(savedImpostorManifest.impostor.quality,
                    Is.EqualTo(VoxelImpostorQuality.High));
                Assert.That(AmplifyImpostorIntegration.FindPendingManifestAssetPaths(testRoot),
                    Does.Not.Contain(build.ManifestAssetPath));
                Assert.That(VoxelLodPipeline.RebuildPrefab(build.ManifestAssetPath),
                    Is.EqualTo(build.PrefabAssetPath));
                prefab = AssetDatabase.LoadAssetAtPath<GameObject>(build.PrefabAssetPath);
                LODGroup prefabLodGroup = prefab.GetComponent<LODGroup>();
                Assert.That(prefabLodGroup.lodCount, Is.EqualTo(3));
                Assert.That(prefabLodGroup.fadeMode, Is.EqualTo(LODFadeMode.None));
                Assert.That(prefabLodGroup.animateCrossFading, Is.False);
                LOD[] lodsWithImpostor = prefabLodGroup.GetLODs();
                Assert.That(lodsWithImpostor[0].screenRelativeTransitionHeight,
                    Is.EqualTo(adaptiveLods[0].screenRelativeTransitionHeight)
                        .Within(1e-6f));
                Assert.That(lodsWithImpostor[1].screenRelativeTransitionHeight,
                    Is.EqualTo(Mathf.Lerp(
                        adaptiveManifest.lods[1].screenRelativeTransitionHeight,
                        0.01f, 1f / 3f))
                        .Within(1e-6f));
                Assert.That(lodsWithImpostor[2].screenRelativeTransitionHeight,
                    Is.EqualTo(0.01f).Within(1e-6f));
                Assert.That(lodsWithImpostor.All(lod =>
                    Mathf.Approximately(lod.fadeTransitionWidth, 0f)), Is.True);
                Assert.That(lodsWithImpostor[2].renderers, Has.Length.EqualTo(1));
                Assert.That(lodsWithImpostor[2].renderers[0].gameObject.name, Is.EqualTo("Impostor"));
                Assert.That(lodsWithImpostor[1].renderers.All(renderer =>
                    renderer.shadowCastingMode == ShadowCastingMode.Off), Is.True);
                Assert.That(lodsWithImpostor[2].renderers[0].shadowCastingMode,
                    Is.EqualTo(ShadowCastingMode.Off));
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

                Assert.That(AmplifyImpostorIntegration.RemoveFromFamily(
                    build.ManifestAssetPath), Is.EqualTo(organizedPrefabPath));
                Assert.That(VoxelLodPipeline.TryReadManifest(
                    build.ManifestAssetPath, out VoxelLodSetManifest removedManifest), Is.True);
                Assert.That(AmplifyImpostorIntegration.HasConfiguredImpostor(
                    removedManifest), Is.False);
                Assert.That(removedManifest.impostorDisabled, Is.True);
                Assert.That(AssetDatabase.LoadMainAssetAtPath(impostorAssetPath), Is.Not.Null,
                    "El atlas se conserva como caché para una regeneración posterior.");
                Assert.That(AmplifyImpostorIntegration.FindPendingManifestAssetPaths(testRoot),
                    Does.Not.Contain(build.ManifestAssetPath));
                Assert.That(AssetDatabase.LoadAssetAtPath<GameObject>(organizedPrefabPath)
                    .GetComponent<LODGroup>().lodCount, Is.EqualTo(2));
            }
            finally
            {
                if (prefabInstance != null) Object.DestroyImmediate(prefabInstance);
                if (root != null) Object.DestroyImmediate(root);
                if (profile != null && !AssetDatabase.Contains(profile))
                    Object.DestroyImmediate(profile);
                AssetDatabase.DeleteAsset(testRoot);
            }
        }

        [Test]
        public void AutomaticLodPipeline_PersistsAdaptiveBaseForManualReduction()
        {
            if (!VoxelImporterIntegration.IsInstalled)
                Assert.Ignore("Voxel Importer es opcional y no está instalado.");

            const string testRoot = "Assets/VoxelBridgeAdaptiveLodTestOutput";
            GameObject root = null;
            VoxelStyleProfile profile = null;
            try
            {
                root = GameObject.CreatePrimitive(PrimitiveType.Cube);
                root.name = "AdaptiveCube";
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
                    SingleColor = new Color32(120, 160, 200, 255),
                    AlphaCutoff = 0.1f,
                    ExportFolder = testRoot + "/Exports"
                };

                VoxelLodBuildResult build = VoxelLodPipeline.GenerateAutomatic(
                    root, profile, options, null, 2);

                Assert.That(VoxelImporterIntegration.TryLoadMetadata(build.VoxAssetPaths[0],
                    out VoxelBridgeMetadata lod0, out string error), Is.True, error);
                Assert.That(VoxelImporterIntegration.TryLoadMetadata(build.VoxAssetPaths[1],
                    out VoxelBridgeMetadata lod1, out error), Is.True, error);
                Assert.That(lod0.voxelSize, Is.EqualTo(0.5f).Within(1e-6f));
                Assert.That(lod0.lodMultiplier, Is.EqualTo(2));
                Assert.That(lod1.voxelSize, Is.EqualTo(1f).Within(1e-6f));
                Assert.That(lod1.lodMultiplier, Is.EqualTo(4));

                Assert.That(VoxelLodPipeline.TryReadManifest(
                    build.ManifestAssetPath, out VoxelLodSetManifest manifest), Is.True);
                Assert.That(manifest.formatVersion, Is.EqualTo(4));
                Assert.That(manifest.baseVoxelSize, Is.EqualTo(0.25f).Within(1e-6f));
                Assert.That(manifest.initialVoxelMultiplier, Is.EqualTo(2));
                Assert.That(manifest.lods.Select(entry => entry.multiplier),
                    Is.EqualTo(new[] { 2, 4 }));

                VoxelLodBuildResult reduced = VoxelLodPipeline.GenerateManual(
                    build.VoxAssetPaths[0], profile, 1,
                    VoxelLodGenerationMode.ReduceParent, options);
                Assert.That(VoxelImporterIntegration.TryLoadMetadata(reduced.VoxAssetPaths[0],
                    out VoxelBridgeMetadata reducedMetadata, out error), Is.True, error);
                Assert.That(reducedMetadata.voxelSize, Is.EqualTo(1f).Within(1e-6f));
                Assert.That(reducedMetadata.lodMultiplier, Is.EqualTo(4));
            }
            finally
            {
                if (root != null) Object.DestroyImmediate(root);
                if (profile != null) Object.DestroyImmediate(profile);
                AssetDatabase.DeleteAsset(testRoot);
            }
        }

        [Test]
        public void AutomaticLodPipeline_AdaptsOccupiedVoxelCountBeforeImport()
        {
            if (!VoxelImporterIntegration.IsInstalled)
                Assert.Ignore("Voxel Importer es opcional y no está instalado.");

            const string testRoot = "Assets/VoxelBridgeImporterLimitTestOutput";
            GameObject root = null;
            VoxelStyleProfile profile = null;
            try
            {
                root = GameObject.CreatePrimitive(PrimitiveType.Cube);
                root.name = "ImporterLimitCube";
                profile = ScriptableObject.CreateInstance<VoxelStyleProfile>();
                var serializedProfile = new SerializedObject(profile);
                serializedProfile.FindProperty("baseVoxelSize").floatValue = 0.25f;
                serializedProfile.FindProperty("chunkCellSize").intValue = 16;
                serializedProfile.FindProperty("padding").intValue = 1;
                serializedProfile.FindProperty("fillInterior").boolValue = true;
                SerializedProperty multipliers = serializedProfile.FindProperty("lodMultipliers");
                multipliers.arraySize = 3;
                multipliers.GetArrayElementAtIndex(0).intValue = 1;
                multipliers.GetArrayElementAtIndex(1).intValue = 2;
                multipliers.GetArrayElementAtIndex(2).intValue = 4;
                serializedProfile.ApplyModifiedPropertiesWithoutUndo();

                VoxelizationResult fine = MeshVoxelizer.Voxelize(root, new VoxelizationSettings
                {
                    VoxelSize = 0.25f,
                    ChunkCellSize = 16,
                    Padding = 1,
                    FillInterior = true,
                    ColorMode = VoxelColorMode.SingleColor
                });
                VoxelizationResult coarse = MeshVoxelizer.Voxelize(root, new VoxelizationSettings
                {
                    VoxelSize = 0.5f,
                    ChunkCellSize = 16,
                    Padding = 1,
                    FillInterior = true,
                    ColorMode = VoxelColorMode.SingleColor
                });
                Assert.That(fine.OccupiedVoxelCount, Is.GreaterThan(coarse.OccupiedVoxelCount));
                int importerLimit = coarse.OccupiedVoxelCount +
                                    (fine.OccupiedVoxelCount - coarse.OccupiedVoxelCount) / 2;

                var options = new VoxelLodBuildOptions
                {
                    ColorMode = VoxelColorMode.SingleColor,
                    SingleColor = new Color32(100, 140, 180, 255),
                    AlphaCutoff = 0.1f,
                    ExportFolder = testRoot + "/Accepted"
                };
                VoxelLodBuildResult build = VoxelLodPipeline.GenerateAutomatic(
                    root, profile, options, null, 1, importerLimit, 4);

                Assert.That(VoxelImporterIntegration.TryLoadMetadata(build.VoxAssetPaths[0],
                    out VoxelBridgeMetadata lod0, out string error), Is.True, error);
                Assert.That(lod0.lodMultiplier, Is.EqualTo(2));
                Assert.That(lod0.voxelSize, Is.EqualTo(0.5f).Within(1e-6f));
                Assert.That(lod0.voxelCount, Is.LessThanOrEqualTo(importerLimit));
                Assert.That(VoxelLodPipeline.TryReadManifest(
                    build.ManifestAssetPath, out VoxelLodSetManifest manifest), Is.True);
                Assert.That(manifest.initialVoxelMultiplier, Is.EqualTo(2));

                var rejectedOptions = new VoxelLodBuildOptions
                {
                    ColorMode = VoxelColorMode.SingleColor,
                    ExportFolder = testRoot + "/Rejected"
                };
                System.InvalidOperationException exception = Assert.Throws<System.InvalidOperationException>(() =>
                    VoxelLodPipeline.GenerateAutomatic(
                        root, profile, rejectedOptions, null, 1, importerLimit, 1));
                Assert.That(exception.Message, Does.Contain("por encima del límite de importación"));
                Assert.That(AssetDatabase.IsValidFolder(rejectedOptions.ExportFolder), Is.False);
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

        private static string SaveImpostorPlanManifest(
            string folder, string name, float lodGroupSize,
            bool impostorDisabled = false)
        {
            string path = $"{folder}/{name}.voxset.json";
            VoxelLodPipeline.SaveManifest(path, new VoxelLodSetManifest
            {
                sourceName = name,
                lodGroupSize = lodGroupSize,
                impostorDisabled = impostorDisabled
            });
            return path;
        }

        private static void SetWindowField<T>(VoxelBridgeWindow window, string fieldName, T value)
        {
            FieldInfo field = typeof(VoxelBridgeWindow).GetField(
                fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, $"No se encontró el campo '{fieldName}'.");
            field.SetValue(window, value);
        }

        private static T GetWindowField<T>(VoxelBridgeWindow window, string fieldName)
        {
            FieldInfo field = typeof(VoxelBridgeWindow).GetField(
                fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
            Assert.That(field, Is.Not.Null, $"No se encontró el campo '{fieldName}'.");
            return (T)field.GetValue(window);
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
