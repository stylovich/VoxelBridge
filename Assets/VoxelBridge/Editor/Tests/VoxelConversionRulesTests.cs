using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace LocalModels.VoxelBridge.Tests
{
    public sealed class VoxelConversionRulesTests
    {
        private string folder;
        private Scene scene;
        private GameObject root;
        private VoxelConversionProfile conversion;
        private VoxelStyleProfile style;
        private Material body, glass;
        private string sharedMaterial;

        [SetUp]
        public void SetUp()
        {
            folder = "Assets/VoxelRulesTest_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder));
            scene = EditorSceneManager.NewPreviewScene();
            root = new GameObject("Source");
            SceneManager.MoveGameObjectToScene(root, scene);
            var colors = ScriptableObject.CreateInstance<VoxelColorPalette>();
            var surfaces = ScriptableObject.CreateInstance<VoxelSurfacePalette>();
            AssetDatabase.CreateAsset(colors, folder + "/Colors.asset");
            AssetDatabase.CreateAsset(surfaces, folder + "/Surfaces.asset");
            Assert.That(VoxelPaletteLutGenerator.TryRebuild(colors, out _, out string error), Is.True, error);
            Assert.That(VoxelPaletteLutGenerator.TryRebuild(surfaces, out _, out error), Is.True, error);
            sharedMaterial = VoxelProductionExporter.SharedMaterialFolder + "/VoxelWorld_" + Hash128.Compute(
                AssetDatabase.AssetPathToGUID(folder + "/Colors.asset") + ":" +
                AssetDatabase.AssetPathToGUID(folder + "/Surfaces.asset")) + ".mat";
            var mapping = ScriptableObject.CreateInstance<VoxelColorMappingProfile>();
            mapping.ConfigureForTests(colors, 100, 100);
            AssetDatabase.CreateAsset(mapping, folder + "/Mapping.asset");
            conversion = ScriptableObject.CreateInstance<VoxelConversionProfile>();
            conversion.colorMapping = mapping; conversion.surfacePalette = surfaces; conversion.detectEmission = false;
            AssetDatabase.CreateAsset(conversion, folder + "/Conversion.asset");
            style = ScriptableObject.CreateInstance<VoxelStyleProfile>();
            AssetDatabase.CreateAsset(style, folder + "/Style.asset");
            var data = new SerializedObject(style);
            data.FindProperty("baseVoxelSize").floatValue = .125f;
            data.FindProperty("chunkCellSize").intValue = 16;
            data.ApplyModifiedPropertiesWithoutUndo();
            body = new Material(Shader.Find("HDRP/Lit")); body.SetColor("_BaseColor", Color.white);
            glass = new Material(Shader.Find("HDRP/Lit")); glass.SetColor("_BaseColor", Color.white);
            AssetDatabase.CreateAsset(body, folder + "/Body.mat");
            AssetDatabase.CreateAsset(glass, folder + "/Glass.mat");
        }

        [TearDown]
        public void TearDown()
        {
            if (root != null) Object.DestroyImmediate(root);
            if (scene.IsValid()) EditorSceneManager.ClosePreviewScene(scene);
            if (sharedMaterial != null) AssetDatabase.DeleteAsset(sharedMaterial);
            if (folder != null) AssetDatabase.DeleteAsset(folder);
        }

        private GameObject Cube(Vector3 position, Material material)
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            SceneManager.MoveGameObjectToScene(cube, scene);
            cube.transform.SetParent(root.transform, false); cube.transform.localPosition = position;
            cube.GetComponent<MeshRenderer>().sharedMaterial = material;
            return cube;
        }

        private static void SetRule(GameObject go, VoxelConversionAction action, int surface = -1)
        {
            var marker = go.GetComponent<VoxelConversionRule>();
            if (marker == null) marker = go.AddComponent<VoxelConversionRule>();
            var data = new SerializedObject(marker);
            data.FindProperty("action").enumValueIndex = (int)action;
            data.FindProperty("surfaceId").intValue = surface;
            data.ApplyModifiedPropertiesWithoutUndo();
        }

        private VoxelizationResult Voxelize() => MeshVoxelizer.Voxelize(root, new VoxelizationSettings
        { VoxelSize = .125f, Padding = 1, FillInterior = true, ConversionProfile = conversion });

        [Test]
        public void SourceAxes_NormalizesBoundsMeshesAndRetainedGeometryWithoutChangingSource()
        {
            var cube = Cube(new Vector3(1, 2, 3), body);
            cube.transform.localScale = new Vector3(2, 4, 6);
            root.transform.rotation = Quaternion.Euler(270, 0, 0);
            Quaternion sourceRotation = root.transform.rotation;
            string fingerprint = VoxelConversionProfile.Fingerprint(conversion);
            var original = Voxelize();
            conversion.sourceAxes = VoxelSourceAxes.ZUp;
            Assert.That(VoxelConversionProfile.Fingerprint(conversion), Is.Not.EqualTo(fingerprint));
            var normalized = Voxelize();
            VoxelSourceOrientationTests.AssertBounds(new Bounds(new Vector3(1, 3, -2), new Vector3(2, 6, 4)), normalized.SourceBounds);
            VoxelSourceOrientationTests.AssertBounds(normalized.SourceBounds, MeshVoxelizer.GetSourceBounds(root, true, conversion));
            VoxelSourceOrientationTests.AssertBounds(new Bounds(new Vector3(1, 2, 3), new Vector3(2, 4, 6)), original.SourceBounds);
            Assert.That(root.transform.rotation, Is.EqualTo(sourceRotation));
            Assert.That(cube.transform.localPosition, Is.EqualTo(new Vector3(1, 2, 3)));
            var prefab = PrefabUtility.SaveAsPrefabAsset(root, folder + "/ZUp.prefab");
            VoxelSourceOrientationTests.AssertBounds(normalized.SourceBounds, MeshVoxelizer.GetSourceBounds(prefab, true, conversion));
            var mesh = Object.Instantiate(cube.GetComponent<MeshFilter>().sharedMesh);
            try
            {
                mesh.vertices = mesh.vertices.Select(v => Vector3.Scale(v, new Vector3(2, 4, 6)) + new Vector3(1, 2, 3)).ToArray();
                VoxelSourceOrientationTests.AssertBounds(normalized.SourceBounds, MeshVoxelizer.GetSourceBounds(mesh, true, conversion));
            }
            finally { Object.DestroyImmediate(mesh); }
            SetRule(cube, VoxelConversionAction.KeepOriginal);
            string retained = MeshVoxelizer.ExportRetainedGeometry(root, true, conversion, folder);
            var retainedMesh = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(retained)).GetComponentInChildren<MeshFilter>().sharedMesh;
            VoxelSourceOrientationTests.AssertBounds(normalized.SourceBounds, retainedMesh.bounds);
        }

        [Test]
        public void SourceAxes_BatchPlacementUsesEachSavedFamilyBasis()
        {
            var first = Cube(new Vector3(1, 2, 3), body); first.name = "Z Up Source";
            var second = Cube(new Vector3(4, 5, 6), body); second.name = "Preserved Source";
            first.transform.localRotation = Quaternion.Euler(270, 12, 0);
            first.transform.localScale = new Vector3(2, -3, 4);
            second.transform.localRotation = Quaternion.Euler(20, 32, 18);
            second.transform.localScale = new Vector3(-3, 4, 2);
            root.transform.rotation = Quaternion.Euler(11, 22, 33);
            root.transform.localScale = new Vector3(1.5f, 2, .5f);
            var options = new VoxelLodBuildOptions { ExportFolder = folder, ConversionProfile = conversion, GenerateLod0Only = true };
            conversion.sourceAxes = VoxelSourceAxes.ZUp;
            var firstBuild = VoxelLodPipeline.GenerateAutomatic(first, style, options);
            conversion.sourceAxes = VoxelSourceAxes.PreserveLocalAxes;
            var secondBuild = VoxelLodPipeline.GenerateAutomatic(second, style, options);
            var batch = new VoxelLodBatchBuildResult(new[]
            {
                new VoxelLodBatchItemResult(new VoxelLodBatchSourcePlan(first, first, first, false, false), firstBuild),
                new VoxelLodBatchItemResult(new VoxelLodBatchSourcePlan(second, second, second, false, false), secondBuild)
            }, 2, false);
            GameObject placed = null;
            try
            {
                SceneManager.MoveGameObjectToScene(root, SceneManager.GetActiveScene());
                placed = VoxelLodBatchScenePlacement.Place(root, batch, null, false).Root;
                VoxelSourceOrientationTests.AssertEquivalent(first.transform.localToWorldMatrix,
                    placed.transform.Find(first.name).localToWorldMatrix * VoxelSourceOrientation.ToUnity(VoxelSourceAxes.ZUp));
                VoxelSourceOrientationTests.AssertEquivalent(second.transform.localToWorldMatrix,
                    placed.transform.Find(second.name).localToWorldMatrix);
            }
            finally
            {
                if (placed != null) Object.DestroyImmediate(placed);
                SceneManager.MoveGameObjectToScene(root, scene);
            }
        }

        [Test]
        public void SourceAxes_PersistsAcrossDerivedLodsAndRebuildWithoutDoubleRotation()
        {
            var cube = Cube(new Vector3(0, 0, 2), body); cube.transform.localScale = new Vector3(1, 2, 3);
            conversion.sourceAxes = VoxelSourceAxes.ZUp;
            var build = VoxelLodPipeline.GenerateAutomatic(root, style,
                new VoxelLodBuildOptions { ExportFolder = folder, ConversionProfile = conversion, GenerateLod0Only = true });
            var initial = VoxelProductionExporter.ReadGrid(build.VoxAssetPaths[0], out var metadata, out _, out _, out _);
            Assert.That(metadata.sourceAxes, Is.EqualTo(VoxelSourceAxes.ZUp));
            Assert.That(metadata.sourceBoundsMax.y - metadata.sourceBoundsMin.y, Is.EqualTo(3));
            conversion.sourceAxes = VoxelSourceAxes.PreserveLocalAxes;
            VoxelProductionFamily.DeriveLevel(build.ManifestAssetPath, 1, VoxelLodGenerationMode.DuplicateParent);
            VoxelProductionFamily.DeriveLevel(build.ManifestAssetPath, 2, VoxelLodGenerationMode.ReduceParent);
            VoxelProductionFamily.RebuildAll(build.ManifestAssetPath);
            var manifest = VoxelProductionFamily.Load(build.ManifestAssetPath);
            Assert.That(manifest.sourceAxes, Is.EqualTo(VoxelSourceAxes.ZUp));
            Assert.That(VoxelSourceOrientation.ReadPlacementAxes(build.ManifestAssetPath), Is.EqualTo(VoxelSourceAxes.ZUp));
            foreach (var entry in manifest.lods)
            {
                var grid = VoxelProductionExporter.ReadGrid(VoxelProductionFamily.SourcePath(entry), out var level, out _, out _, out _);
                Assert.That(level.sourceAxes, Is.EqualTo(VoxelSourceAxes.ZUp));
                Assert.That(level.sourceBoundsMin, Is.EqualTo(metadata.sourceBoundsMin));
                Assert.That(level.sourceBoundsMax, Is.EqualTo(metadata.sourceBoundsMax));
                if (entry.lodIndex < 2)
                {
                    Assert.That(grid.Size, Is.EqualTo(initial.Size)); Assert.That(grid.Origin, Is.EqualTo(initial.Origin));
                    Assert.That(grid.Occupied, Is.EqualTo(initial.Occupied)); Assert.That(grid.SemanticIds, Is.EqualTo(initial.SemanticIds));
                }
            }
        }

        private VoxelSurfaceMappingProfile EnablePbr()
        {
            var mapping = ScriptableObject.CreateInstance<VoxelSurfaceMappingProfile>(); mapping.surfacePalette = conversion.surfacePalette;
            AssetDatabase.CreateAsset(mapping, folder + "/SurfaceMapping.asset");
            conversion.surfaceMapping = mapping; conversion.assignSurfacesFromPbr = true; conversion.detectEmission = true;
            return mapping;
        }

        [Test]
        public void PbrMapping_PreservesExplicitAndEmissionPriorityAndInvalidatesCheckpoint()
        {
            var solid = Cube(Vector3.zero, body); Cube(new Vector3(2, 0, 0), glass);
            body.SetFloat("_Metallic", 1); body.SetFloat("_Smoothness", .66f);
            glass.SetColor("_EmissiveColor", Color.white * 4);
            Assert.That(Voxelize().SurfaceReport, Is.Null, "PBR mapping is opt-in.");
            var mapping = EnablePbr();
            var result = Voxelize();
            Assert.That(result.Grid.SemanticIds.Where((id, i) => result.Grid.Occupied[i]).Select(VoxelSemanticEncoding.SurfaceId).Distinct(), Is.EquivalentTo(new[] { 6, 13 }));
            Assert.That(result.SurfaceReport.entries.Any(e => e.decision == "Automatic" && e.surfaceId == 6), Is.True);
            Assert.That(result.SurfaceReport.entries.Any(e => e.decision == "EmissiveFallback" && e.surfaceId == 13), Is.True);
            Assert.That(result.SurfaceReport.entries.Sum(e => e.cells), Is.EqualTo(result.SurfaceReport.sampledCells));
            Assert.That(result.SurfaceReport.sampledCells + result.SurfaceReport.filledInteriorCells, Is.EqualTo(result.OccupiedVoxelCount));
            string fingerprint = VoxelConversionProfile.Fingerprint(conversion);
            mapping.maximumDistance *= .5f;
            Assert.That(VoxelConversionProfile.Fingerprint(conversion), Is.Not.EqualTo(fingerprint));
            conversion.materialRules.Add(new VoxelMaterialConversionRule { material = body, surfaceId = 7 });
            SetRule(solid, VoxelConversionAction.Voxelize, 0);
            result = Voxelize();
            Assert.That(result.Grid.SemanticIds.Where((id, i) => result.Grid.Occupied[i]).Select(VoxelSemanticEncoding.SurfaceId).Distinct(), Is.EquivalentTo(new[] { 0, 13 }));
            Assert.That(result.SurfaceReport.entries.Any(e => e.decision == "Explicit" && e.surfaceId == 0), Is.True);
        }

        [Test]
        public void PbrMapping_SingleColorWithoutMaterialPreservesChosenColor()
        {
            var cube = Cube(Vector3.zero, body);
            EnablePbr();
            conversion.colorMapping.ColorPalette.TryGetColor(54, out var color);
            int expectedColor = VoxelSemanticEncoding.ColorId(new VoxelConversionColorMapper(conversion).Map(color, -1));
            var result = MeshVoxelizer.Voxelize(cube.GetComponent<MeshFilter>().sharedMesh, new VoxelizationSettings
            { VoxelSize = .125f, ColorMode = VoxelColorMode.SingleColor, SingleColor = color, ConversionProfile = conversion });
            Assert.That(result.Grid.SemanticIds.Where((id, i) => result.Grid.Occupied[i]).Select(VoxelSemanticEncoding.ColorId).Distinct(), Is.EquivalentTo(new[] { expectedColor }));
            Assert.That(result.SurfaceReport.entries.All(e => e.decision == "Unsupported"), Is.True);
        }

        [Test]
        public void PbrMapping_SingleColorKeepsColorWhileRecognizingEmission()
        {
            Cube(Vector3.zero, body); EnablePbr();
            body.SetColor("_EmissiveColor", Color.red * 4);
            conversion.colorMapping.ColorPalette.TryGetColor(54, out var color);
            int expectedColor = VoxelSemanticEncoding.ColorId(new VoxelConversionColorMapper(conversion).Map(color, -1));
            var result = MeshVoxelizer.Voxelize(root, new VoxelizationSettings
            { VoxelSize = .125f, ColorMode = VoxelColorMode.SingleColor, SingleColor = color, ConversionProfile = conversion });
            Assert.That(result.Grid.SemanticIds.Where((id, i) => result.Grid.Occupied[i]).Distinct(), Is.EquivalentTo(new[] { VoxelSemanticEncoding.Pack(expectedColor, 13) }));
            Assert.That(result.SurfaceReport.entries.All(e => e.decision == "EmissiveFallback"), Is.True);
        }

        [Test]
        public void PbrMapping_MaskRegionsReachVoxelIdsAndMissingUvsUseFallback()
        {
            var cube = Cube(Vector3.zero, body);
            var mapping = EnablePbr(); mapping.candidateSurfaceIds = new System.Collections.Generic.List<int> { 3, 6 };
            var texture = new Texture2D(2, 1, TextureFormat.RGBA32, false, true) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Point };
            texture.SetPixels(new[] { new Color(0, .5f, .3f, .28f), new Color(1, .2f, .8f, .66f) }); texture.Apply();
            AssetDatabase.CreateAsset(texture, folder + "/Mask.asset");
            body.SetTexture("_MaskMap", texture); body.EnableKeyword("_MASKMAP");
            var result = Voxelize();
            Assert.That(result.Grid.SemanticIds.Where((id, i) => result.Grid.Occupied[i]).Select(VoxelSemanticEncoding.SurfaceId).Distinct(), Is.EquivalentTo(new[] { 3, 6 }));
            Assert.That(result.SurfaceReport.entries.All(e => e.decision == "Automatic"), Is.True);
            var mesh = Object.Instantiate(cube.GetComponent<MeshFilter>().sharedMesh); mesh.uv = Array.Empty<Vector2>();
            AssetDatabase.CreateAsset(mesh, folder + "/NoUv.asset"); cube.GetComponent<MeshFilter>().sharedMesh = mesh;
            result = Voxelize();
            Assert.That(result.SurfaceReport.entries.All(e => e.decision == "Unsupported" && e.surfaceId == conversion.defaultSurfaceId), Is.True);
        }

        [Test]
        public void PbrMapping_UnsupportedAndAmbiguousSamplesKeepFallback()
        {
            Cube(Vector3.zero, body);
            var mapping = EnablePbr(); mapping.candidateSurfaceIds = new System.Collections.Generic.List<int> { 3, 9 };
            body.SetFloat("_Metallic", 0); body.SetFloat("_Smoothness", .26f);
            var result = Voxelize();
            Assert.That(result.SurfaceReport.entries.All(e => e.decision == "Ambiguous" && e.surfaceId == conversion.defaultSurfaceId), Is.True);
            body.SetTexture("_MaskMap", Texture2D.whiteTexture); body.EnableKeyword("_MASKMAP"); body.SetFloat("_UVBase", 2);
            result = Voxelize();
            Assert.That(result.SurfaceReport.entries.All(e => e.decision == "Unsupported" && e.surfaceId == conversion.defaultSurfaceId), Is.True);
            Assert.That(result.SurfaceReport.warnings.Length, Is.GreaterThan(0));
            conversion.detectEmission = false;
            Assert.Throws<InvalidDataException>(() => conversion.Validate());
        }

        [Test]
        public void Rules_ValidateIdsDuplicatesAndMarkerPrecedence()
        {
            var cube = Cube(Vector3.zero, body);
            conversion.materialRules.Add(new VoxelMaterialConversionRule { material = body, action = VoxelConversionAction.Ignore });
            Assert.Throws<InvalidOperationException>(() => Voxelize());
            SetRule(cube, VoxelConversionAction.Voxelize, 7);
            var result = Voxelize();
            Assert.That(result.Grid.SemanticIds.Where((id, i) => result.Grid.Occupied[i]).All(id => VoxelSemanticEncoding.SurfaceId(id) == 7), Is.True);
            Assert.That(VoxelResolvedRule.Resolve(root.transform, cube.transform, body, conversion).SurfaceId, Is.EqualTo(7));
            SetRule(cube, VoxelConversionAction.Voxelize, 250);
            Assert.Throws<InvalidDataException>(() => Voxelize());
            conversion.materialRules.Add(new VoxelMaterialConversionRule { material = body });
            Assert.Throws<InvalidDataException>(() => conversion.Validate());
        }

        [Test]
        public void Ignore_ExcludesBoundsAndInvalidatesAnalysisAndCheckpoint()
        {
            Cube(Vector3.zero, body);
            var huge = Cube(new Vector3(10000, 0, 0), glass);
            SetRule(huge, VoxelConversionAction.Ignore);
            var bounds = MeshVoxelizer.GetSourceBounds(root, true, conversion);
            Assert.That(bounds.size.x, Is.EqualTo(1).Within(.001));
            var plan = new VoxelLodBatchSourcePlan(root, root, root, false, false);
            var options = new VoxelLodBuildOptions { ExportFolder = folder, ConversionProfile = conversion, GenerateLod0Only = true };
            var safety = new VoxelLodBatchOptions();
            string signature = VoxelLodBatchAnalyzer.CreateSignature(new[] { plan }, style, options, safety);
            string checkpoint = VoxelLodBatchIdentity.CreateBatchSignature(new[] { plan }, style, options, safety);
            SetRule(huge, VoxelConversionAction.KeepOriginal);
            Assert.That(VoxelLodBatchAnalyzer.CreateSignature(new[] { plan }, style, options, safety), Is.Not.EqualTo(signature));
            Assert.That(VoxelLodBatchIdentity.CreateBatchSignature(new[] { plan }, style, options, safety), Is.Not.EqualTo(checkpoint));
            Assert.That(MeshVoxelizer.GetSourceBounds(root, true, conversion), Is.EqualTo(bounds));
        }

        [Test]
        public void WindowExclusion_OpensFloodFillAndRetainsOnlyItsSubmesh()
        {
            var cube = Cube(Vector3.zero, body);
            Mesh mesh = Object.Instantiate(cube.GetComponent<MeshFilter>().sharedMesh);
            var triangles = mesh.triangles; var normals = mesh.normals;
            var window = Enumerable.Range(0, triangles.Length / 3).Where(t => normals[triangles[t * 3]].z > .5f).ToArray();
            mesh.subMeshCount = 2;
            mesh.SetTriangles(Enumerable.Range(0, triangles.Length / 3).Except(window).SelectMany(t => triangles.Skip(t * 3).Take(3)).ToArray(), 0);
            mesh.SetTriangles(window.SelectMany(t => triangles.Skip(t * 3).Take(3)).ToArray(), 1);
            AssetDatabase.CreateAsset(mesh, folder + "/Shell.asset");
            cube.GetComponent<MeshFilter>().sharedMesh = mesh;
            cube.GetComponent<MeshRenderer>().sharedMaterials = new[] { body, glass };
            bool Center(VoxelGrid grid) { var p = Vector3Int.FloorToInt(-grid.Origin / grid.VoxelSize); return grid.Occupied[grid.Index(p.x, p.y, p.z)]; }
            Assert.That(Center(Voxelize().Grid), Is.True);
            conversion.materialRules.Add(new VoxelMaterialConversionRule { material = glass, action = VoxelConversionAction.KeepOriginal });
            Assert.That(Center(Voxelize().Grid), Is.False);
            string guid = MeshVoxelizer.ExportRetainedGeometry(root, true, conversion, folder);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid));
            Assert.That(prefab.GetComponentsInChildren<MeshRenderer>().Length, Is.EqualTo(1));
            var retained = prefab.GetComponentInChildren<MeshFilter>().sharedMesh;
            Assert.That(retained.triangles.Length, Is.EqualTo(6));
            Assert.That(retained.bounds.size.z, Is.LessThan(.001));
            Assert.That(prefab.GetComponentsInChildren<Collider>().Length, Is.Zero);
            Assert.That(prefab.GetComponentsInChildren<MonoBehaviour>().Length, Is.Zero);
        }

        [Test]
        public void Emission_SeparatesIdenticalColorsAndExplicitSurfaceWins()
        {
            Cube(Vector3.zero, body); var neon = Cube(new Vector3(2, 0, 0), glass);
            conversion.detectEmission = true;
            glass.SetColor("_EmissiveColor", Color.white * 4);
            var result = Voxelize();
            var ids = result.Grid.SemanticIds.Where((id, i) => result.Grid.Occupied[i]).Distinct().ToArray();
            Assert.That(ids.Select(VoxelSemanticEncoding.ColorId).Distinct().Count(), Is.EqualTo(1));
            Assert.That(ids.Select(VoxelSemanticEncoding.SurfaceId), Is.EquivalentTo(new[] { 0, 13 }));
            SetRule(neon, VoxelConversionAction.Voxelize, 7);
            result = Voxelize();
            Assert.That(result.Grid.SemanticIds.Where((id, i) => result.Grid.Occupied[i]).Select(VoxelSemanticEncoding.SurfaceId).Distinct(), Is.EquivalentTo(new[] { 0, 7 }));
        }

        [Test]
        public void EmissionMap_UsesUvThresholdAndShaderKeyword()
        {
            Cube(Vector3.zero, body);
            var texture = new Texture2D(2, 1, TextureFormat.RGBA32, false, true) { wrapMode = TextureWrapMode.Clamp };
            texture.SetPixels(new[] { Color.black, Color.white }); texture.Apply();
            AssetDatabase.CreateAsset(texture, folder + "/Emission.asset");
            body.SetColor("_EmissiveColor", Color.white);
            body.SetTexture("_EmissiveColorMap", texture);
            body.EnableKeyword("_EMISSIVE_COLOR_MAP");
            conversion.detectEmission = true; conversion.emissionThreshold = .5f;
            int[] Surfaces()
            {
                var grid = Voxelize().Grid;
                return grid.SemanticIds.Where((id, i) => grid.Occupied[i]).Select(VoxelSemanticEncoding.SurfaceId).Distinct().ToArray();
            }
            Assert.That(Surfaces(), Is.EquivalentTo(new[] { 0, 13 }));
            body.DisableKeyword("_EMISSIVE_COLOR_MAP");
            Assert.That(Surfaces(), Is.EquivalentTo(new[] { 13 }));
            body.SetColor("_EmissiveColor", Color.white * .001f);
            Assert.That(Surfaces(), Is.EquivalentTo(new[] { 0 }));
        }

        [TestCase(VoxelSourceAxes.PreserveLocalAxes)]
        [TestCase(VoxelSourceAxes.ZUp)]
        public void RetainedGeometry_PreservesMirroredFaceOrientation(VoxelSourceAxes axes)
        {
            conversion.sourceAxes = axes;
            var cube = Cube(Vector3.zero, glass);
            cube.transform.localScale = new Vector3(-1, 2, 1);
            SetRule(cube, VoxelConversionAction.KeepOriginal);
            string guid = MeshVoxelizer.ExportRetainedGeometry(root, true, conversion, folder);
            var mesh = AssetDatabase.LoadAssetAtPath<GameObject>(AssetDatabase.GUIDToAssetPath(guid)).GetComponentInChildren<MeshFilter>().sharedMesh;
            var vertices = mesh.vertices; var normals = mesh.normals; var triangles = mesh.triangles;
            for (int i = 0; i < triangles.Length; i += 3)
            {
                int a = triangles[i], b = triangles[i + 1], c = triangles[i + 2];
                Assert.That(Vector3.Dot(Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]), normals[a]), Is.GreaterThan(0));
            }
        }

        [Test]
        public void Cancellation_RemovesOnlyTheIncompleteFamilyIncludingRetainedAssets()
        {
            Cube(Vector3.zero, body); Cube(new Vector3(2, 0, 0), glass);
            conversion.materialRules.Add(new VoxelMaterialConversionRule { material = glass, action = VoxelConversionAction.KeepOriginal });
            string[] before = AssetDatabase.GetSubFolders(folder);
            Assert.Throws<OperationCanceledException>(() => VoxelLodPipeline.GenerateAutomatic(root, style,
                new VoxelLodBuildOptions { ExportFolder = folder, ConversionProfile = conversion, ColorMode = VoxelColorMode.MaterialOnly },
                (progress, message) => message.StartsWith("LOD 1:")));
            Assert.That(AssetDatabase.GetSubFolders(folder), Is.EquivalentTo(before));
            Assert.That(AssetDatabase.LoadAssetAtPath<Material>(folder + "/Glass.mat"), Is.EqualTo(glass));
        }

        [Test]
        public void Batch_ReusesPrefabSourcesAndKeepsProfileAliveAcrossCleanup()
        {
            EnablePbr();
            body.SetFloat("_Metallic", 1); body.SetFloat("_Smoothness", .66f);
            var template = Cube(Vector3.zero, body);
            var prefab = PrefabUtility.SaveAsPrefabAsset(template, folder + "/Template.prefab");
            Object.DestroyImmediate(template);
            var first = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
            var second = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
            first.transform.SetParent(root.transform, false); second.transform.SetParent(root.transform, false);
            second.transform.localPosition = new Vector3(3, 0, 0);
            Cube(new Vector3(5, 0, 0), body);
            var options = new VoxelLodBuildOptions { ExportFolder = folder, ConversionProfile = conversion, GenerateLod0Only = true, ColorMode = VoxelColorMode.MaterialOnly };
            var batch = new VoxelLodBatchOptions { EnableCheckpoint = false, CleanupInterval = 1 };
            var result = VoxelLodPipeline.GenerateAutomaticBatch(root, style, options, batchOptions: batch);
            Assert.That(result.FailedCount, Is.Zero, string.Join("; ", result.Items.Select(i => i.Error)));
            Assert.That(result.CreatedFamilyCount, Is.EqualTo(2));
            Assert.That(result.ReusedCount, Is.EqualTo(1));
            Assert.That(conversion != null && conversion.colorMapping != null && conversion.surfacePalette != null, Is.True);
            Assert.That(conversion.surfaceMapping != null, Is.True);
            foreach (var item in result.Items)
            {
                Assert.That(VoxelImporterIntegration.TryLoadMetadata(item.BuildResult.VoxAssetPaths.Single(), out var metadata, out string error), Is.True, error);
                Assert.That(metadata.formatVersion, Is.EqualTo(4));
                Assert.That(VoxelProductionFamily.Load(item.BuildResult.ManifestAssetPath).productionMeshes, Is.True);
                Assert.That(VoxelProductionLink.HasLink(item.BuildResult.PrefabAssetPath), Is.True);
                string reportPath = Path.ChangeExtension(item.BuildResult.VoxAssetPaths.Single(), ".surface-report.json");
                var report = JsonUtility.FromJson<VoxelSurfaceAssignmentReport>(File.ReadAllText(reportPath));
                Assert.That(report.entries.Any(e => e.decision == "Automatic" && e.surfaceId == 6), Is.True);
            }
            SetRule(first, VoxelConversionAction.Voxelize, 7);
            batch.ModifiedPrefabHandling = VoxelPrefabOverrideHandling.ConvertInstanceSeparately;
            var plans = VoxelLodPipeline.GetAutomaticBatchPlans(root, batch);
            Assert.That(plans.Single(p => p.Source == first).ConversionSource, Is.EqualTo(first));
            Assert.That(plans.Single(p => p.Source == second).ConversionSource, Is.EqualTo(prefab));
        }

        [Test]
        public void AutomaticFamily_RoundTripsSemanticsAndRetainedGeometryThroughProductionLods()
        {
            Cube(Vector3.zero, body); Cube(new Vector3(1.5f, 0, 0), glass);
            conversion.materialRules.Add(new VoxelMaterialConversionRule { material = body, surfaceId = 7 });
            conversion.materialRules.Add(new VoxelMaterialConversionRule { material = glass, action = VoxelConversionAction.KeepOriginal });
            Assert.That(VoxelPaletteLutGenerator.TryRebuild(conversion.colorMapping.ColorPalette, out _, out string error), Is.True, error);
            Assert.That(VoxelPaletteLutGenerator.TryRebuild(conversion.surfacePalette, out _, out error), Is.True, error);
            var build = VoxelLodPipeline.GenerateAutomatic(root, style, new VoxelLodBuildOptions
            { ExportFolder = folder, GenerateLod0Only = true, ColorMode = VoxelColorMode.MaterialOnly, ConversionProfile = conversion });
            string vox = build.VoxAssetPaths.Single();
            Assert.That(VoxelImporterIntegration.TryLoadMetadata(vox, out var metadata, out error), Is.True, error);
            Assert.That(metadata.formatVersion, Is.EqualTo(4));
            Assert.That(metadata.semantic.slots.All(s => s.surfaceId == 7), Is.True);
            Assert.That(metadata.retainedGeometryGuid, Is.Not.Null.And.Not.Empty);
            var production = AssetDatabase.LoadAssetAtPath<GameObject>(build.PrefabAssetPath);
            Assert.That(production.GetComponentInChildren<MeshRenderer>().GetComponents<VoxelConversionRule>(), Is.Empty);
            string manifestPath = VoxelProductionFamily.Create(vox, style, folder + "/Production");
            Assert.That(manifestPath, Is.EqualTo(build.ManifestAssetPath));
            Assert.That(AssetDatabase.IsValidFolder(folder + "/Production"), Is.False, "The same source must not create a second family.");
            var manifest = VoxelProductionFamily.Load(manifestPath);
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(manifest.prefabAssetPath);
            sharedMaterial = AssetDatabase.GetAssetPath(prefab.transform.Find("LOD0").GetComponentInChildren<MeshRenderer>().sharedMaterial);
            VoxelProductionFamily.DeriveLevel(manifestPath, 1, VoxelLodGenerationMode.ReduceParent);
            VoxelProductionFamily.RebuildAll(manifestPath);
            manifest = VoxelProductionFamily.Load(manifestPath);
            prefab = AssetDatabase.LoadAssetAtPath<GameObject>(manifest.prefabAssetPath);
            Assert.That(manifest.retainedGeometryGuid, Is.EqualTo(metadata.retainedGeometryGuid));
            Assert.That(prefab.GetComponentsInChildren<VoxelConversionRule>().Length, Is.Zero);
            foreach (var lod in prefab.GetComponent<LODGroup>().GetLODs())
            {
                Assert.That(lod.renderers.Count(r => r.sharedMaterial == glass), Is.EqualTo(1));
                Assert.That(lod.renderers.Single(r => r.sharedMaterial == glass).bounds.center.x, Is.EqualTo(1.5f).Within(.001));
            }
            Assert.That(VoxelImporterIntegration.TryLoadMetadata(VoxelProductionFamily.SourcePath(manifest.lods[1]), out var child, out error), Is.True, error);
            Assert.That(child.semantic.slots.All(s => s.surfaceId == 7), Is.True);
            Assert.That(child.retainedGeometryGuid, Is.EqualTo(metadata.retainedGeometryGuid));
        }

        [Test]
        public void AutomaticProduction_AllLevelsKeepEmissionLinksScaleAndIndependentSources()
        {
            Cube(Vector3.zero, body);
            conversion.detectEmission = true;
            body.SetColor("_EmissiveColor", Color.white * 50);
            var build = VoxelLodPipeline.GenerateAutomatic(root, style, new VoxelLodBuildOptions
            { ExportFolder = folder, ColorMode = VoxelColorMode.MaterialOnly, ConversionProfile = conversion }, initialVoxelMultiplier: 2);
            var manifest = VoxelProductionFamily.Load(build.ManifestAssetPath);
            Assert.That(manifest.lods.Length, Is.EqualTo(style.LodCount));
            Assert.That(manifest.initialVoxelMultiplier, Is.EqualTo(2));
            Assert.That(manifest.prefabAssetPath, Is.EqualTo(build.PrefabAssetPath));
            Assert.That(VoxelProductionLink.Load(build.PrefabAssetPath).manifestGuid,
                Is.EqualTo(AssetDatabase.AssetPathToGUID(build.ManifestAssetPath)));
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(build.PrefabAssetPath);
            Assert.That(prefab.GetComponent<LODGroup>().fadeMode, Is.EqualTo(UnityEngine.LODFadeMode.None));
            var filters = prefab.GetComponentsInChildren<MeshFilter>();
            Assert.That(filters.All(f => f.sharedMesh.uv4.All(uv => uv.x == 13)), Is.True);
            Assert.That(filters.All(f => AssetDatabase.GetAssetPath(f.sharedMesh).EndsWith(".asset")), Is.True);
            Assert.That(prefab.GetComponentsInChildren<MeshRenderer>().Select(r => r.sharedMaterial).Distinct().Count(), Is.EqualTo(1));
            Assert.That(prefab.GetComponentInChildren<MeshRenderer>().sharedMaterial.shader,
                Is.EqualTo(AssetDatabase.LoadAssetAtPath<Shader>(VoxelProductionExporter.ShaderPath)));
            foreach (var entry in manifest.lods)
                Assert.That(VoxelProductionFamily.IsDerivedStale(manifest, entry.lodIndex), Is.False);
            string firstHash = VoxelProductionFamily.SourceHash(manifest.lods[0]);
            string[] meshGuids = manifest.lods.SelectMany(e => e.meshGuids).ToArray();
            VoxelProductionFamily.RebuildAll(build.ManifestAssetPath);
            manifest = VoxelProductionFamily.Load(build.ManifestAssetPath);
            Assert.That(manifest.lods.SelectMany(e => e.meshGuids), Is.EqualTo(meshGuids));
            Assert.That(VoxelProductionFamily.SourceHash(manifest.lods[0]), Is.EqualTo(firstHash));
            VoxelProductionFamily.DeriveLevel(build.ManifestAssetPath, 1, VoxelLodGenerationMode.ReduceParent, true);
            manifest = VoxelProductionFamily.Load(build.ManifestAssetPath);
            Assert.That(manifest.lods[1].multiplier, Is.EqualTo(2 * style.GetLodMultiplier(1)));
            Assert.That(VoxelImporterIntegration.TryLoadMetadata(VoxelProductionFamily.SourcePath(manifest.lods[1]), out var reduced, out string error), Is.True, error);
            Assert.That(reduced.voxelSize, Is.EqualTo(style.BaseVoxelSize * 2 * style.GetLodMultiplier(1)).Within(.00001));
            Assert.That(VoxelProductionFamily.IsDerivedStale(manifest, 2), Is.False, "Source-mesh levels are independent of edited predecessors.");
        }

        [Test]
        public void ProductionMeshingCancellation_DiscardsIncompleteFamily()
        {
            Cube(Vector3.zero, body);
            string[] before = AssetDatabase.GetSubFolders(folder);
            int calls = 0;
            Assert.Throws<OperationCanceledException>(() => VoxelLodPipeline.GenerateAutomatic(root, style,
                new VoxelLodBuildOptions { ExportFolder = folder, ConversionProfile = conversion },
                (progress, message) => message == "Building production meshes" && ++calls > 2));
            Assert.That(AssetDatabase.GetSubFolders(folder), Is.EquivalentTo(before));
        }

        [Test]
        public void ProductionPreflight_AdaptsToMesherCellLimitBeforeVoxelizing()
        {
            Cube(Vector3.zero, body).transform.localScale = Vector3.one * 30;
            var options = new VoxelLodBuildOptions { ExportFolder = folder, ConversionProfile = conversion, GenerateLod0Only = true };
            var limits = new VoxelLodBatchOptions { AdaptInitialVoxelSize = false, MaximumEstimatedMemoryBytes = 0 };
            var estimate = VoxelLodBatchAnalyzer.AnalyzeSingle(root, style, options, limits);
            Assert.That(estimate.IsValid, Is.False);
            limits.AdaptInitialVoxelSize = true; limits.MaximumInitialLodIndex = 1;
            estimate = VoxelLodBatchAnalyzer.AnalyzeSingle(root, style, options, limits);
            Assert.That(estimate.IsValid, Is.True, estimate.Error);
            Assert.That(estimate.InitialVoxelMultiplier, Is.EqualTo(2));
            Assert.That(estimate.LodPlans.All(p => p.CellCount <= VoxelSemanticMesher.MaximumCells), Is.True);
        }

        [Test]
        public void ConversionWithoutProfile_PreservesRgbPreviewOutput()
        {
            Cube(Vector3.zero, body);
            var build = VoxelLodPipeline.GenerateAutomatic(root, style,
                new VoxelLodBuildOptions { ExportFolder = folder, GenerateLod0Only = true });
            Assert.That(VoxelProductionLink.HasLink(build.PrefabAssetPath), Is.False);
            Assert.That(VoxelLodPipeline.TryReadManifest(build.ManifestAssetPath, out var manifest), Is.True);
            Assert.That(manifest.productionMeshes, Is.False);
            Assert.That(VoxelImporterIntegration.TryLoadMetadata(build.VoxAssetPaths[0], out var metadata, out string error), Is.True, error);
            Assert.That(metadata.formatVersion, Is.EqualTo(3));
        }

        [Test]
        public void ProductionCheckpoint_RejectsMissingChunkMeshes()
        {
            Cube(Vector3.zero, body);
            var options = new VoxelLodBuildOptions { ExportFolder = folder, ConversionProfile = conversion, GenerateLod0Only = true };
            var batch = new VoxelLodBatchOptions();
            var plans = VoxelLodPipeline.GetAutomaticBatchPlans(root, batch);
            string signature = VoxelLodBatchIdentity.CreateBatchSignature(plans, style, options, batch);
            try
            {
                var build = VoxelLodPipeline.GenerateAutomatic(plans[0].ConversionSource, style, options);
                VoxelLodBatchCheckpointStore.Record(signature, plans[0], build);
                Assert.That(VoxelLodBatchCheckpointStore.LoadOutcomes(signature, plans).Count, Is.EqualTo(1));
                var manifest = VoxelProductionFamily.Load(build.ManifestAssetPath);
                AssetDatabase.DeleteAsset(AssetDatabase.GUIDToAssetPath(manifest.lods[0].meshGuids[0]));
                Assert.That(VoxelLodBatchCheckpointStore.LoadOutcomes(signature, plans), Is.Empty);
            }
            finally { VoxelLodBatchCheckpointStore.Reset(signature); }
        }

        [TestCase(VoxelSourceAxes.PreserveLocalAxes)]
        [TestCase(VoxelSourceAxes.ZUp)]
        public void ProductionPlacement_UsesLinkedPrefabAndSupportsUndo(VoxelSourceAxes axes)
        {
            conversion.sourceAxes = axes;
            Cube(Vector3.zero, body);
            var build = VoxelLodPipeline.GenerateAutomatic(root, style,
                new VoxelLodBuildOptions { ExportFolder = folder, ConversionProfile = conversion, GenerateLod0Only = true });
            SceneManager.MoveGameObjectToScene(root, SceneManager.GetActiveScene());
            GameObject instance = null;
            try
            {
                root.transform.position = new Vector3(2, 0, 3);
                root.transform.rotation = Quaternion.Euler(270, 12, 0);
                root.transform.localScale = new Vector3(2, -3, 4);
                conversion.sourceAxes = VoxelSourceAxes.PreserveLocalAxes;
                var placed = VoxelLodBatchScenePlacement.PlaceSingle(root, build, style, true);
                instance = placed.Instance;
                VoxelSourceOrientationTests.AssertEquivalent(root.transform.localToWorldMatrix,
                    instance.transform.localToWorldMatrix * VoxelSourceOrientation.ToUnity(axes));
                Assert.That(root.activeSelf, Is.False);
                Assert.That(PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(instance), Is.EqualTo(build.PrefabAssetPath));
                Assert.That(VoxelProductionLink.HasLink(build.PrefabAssetPath), Is.True);
                Assert.That(instance.transform.position, Is.EqualTo(root.transform.position));
                Assert.That(instance.GetComponentInChildren<MeshRenderer>().sharedMaterial.shader,
                    Is.EqualTo(AssetDatabase.LoadAssetAtPath<Shader>(VoxelProductionExporter.ShaderPath)));
                Undo.FlushUndoRecordObjects();
                Undo.PerformUndo();
                Assert.That(instance == null, Is.True);
                Assert.That(root.activeSelf, Is.True);
            }
            finally
            {
                if (instance != null) Object.DestroyImmediate(instance);
                SceneManager.MoveGameObjectToScene(root, scene);
            }
        }

        [Test]
        public void SemanticImpostors_RejectBeforeCreatingBakeAssets()
        {
            string manifestPath = folder + "/Source.voxset.json";
            VoxelLodPipeline.SaveManifest(manifestPath, new VoxelLodSetManifest
            { productionMeshes = true, sourceName = "Source", lods = new[] { new VoxelLodEntry { lodIndex = 0 } } });
            var profile = ScriptableObject.CreateInstance<VoxelImpostorProfile>();
            try
            {
                Assert.That(AmplifyImpostorIntegration.FindPendingManifestAssetPaths(folder), Is.Empty);
                var error = Assert.Throws<InvalidOperationException>(() =>
                    AmplifyImpostorIntegration.GenerateOrUpdate(manifestPath, profile, VoxelImpostorQuality.Medium));
                StringAssert.Contains("palette-aware bake shader", error.Message);
                Assert.That(AssetDatabase.IsValidFolder(folder + "/Impostor"), Is.False);
            }
            finally { Object.DestroyImmediate(profile); }
        }
    }
}
