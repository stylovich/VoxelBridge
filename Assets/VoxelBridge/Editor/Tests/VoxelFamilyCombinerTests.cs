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
    public sealed class VoxelCellTransformTests
    {
        [TestCase(0, 182.8828f, 0, 0, 180, 0)]
        [TestCase(89, 181, 1, 90, 180, 0)]
        [TestCase(0, 359, 0, 0, 0, 0)]
        public void NearestGridRotation_UsesClosestCubeOrientation(float x, float y, float z, float tx, float ty, float tz)
        {
            var result = VoxelFamilyCombiner.NearestGridRotation(Quaternion.Euler(x, y, z));
            Assert.That(Quaternion.Angle(result, Quaternion.Euler(tx, ty, tz)), Is.LessThan(.01f));
            var metadata = new VoxelBridgeMetadata { voxelSize = .024f, unityGridSize = Vector3Int.one * 200 };
            Assert.DoesNotThrow(() => new VoxelCellTransform(metadata, Matrix4x4.Rotate(result), .024f));
        }

        [TestCase(0, 0, 0, 1)]
        [TestCase(0, 90, 0, 1)]
        [TestCase(90, 180, 270, 1)]
        [TestCase(0, 0, 0, -1)]
        [TestCase(90, 90, 90, -1)]
        public void ExactTransforms_PreserveCellCenters(int rx, int ry, int rz, int sign)
        {
            var metadata = new VoxelBridgeMetadata { voxelSize = .125f, gridOrigin = new Vector3(-.25f, .125f, .5f), unityGridSize = new Vector3Int(20, 3, 2) };
            var matrix = Matrix4x4.TRS(new Vector3(1, 2, -3), Quaternion.Euler(rx, ry, rz), new Vector3(sign, 1, 1));
            var cells = new VoxelCellTransform(metadata, matrix, .125f);
            for (int x = 0; x < 20; x++) for (int y = 0; y < 3; y++) for (int z = 0; z < 2; z++)
            {
                var actual = ((Vector3)cells.Map(x, y, z) + Vector3.one * .5f) * .125f;
                var expected = matrix.MultiplyPoint3x4(metadata.gridOrigin + new Vector3(x + .5f, y + .5f, z + .5f) * .125f);
                Assert.That(Vector3.Distance(actual, expected), Is.LessThan(.00001f));
            }
        }

        [TestCase(45, 0, 1)]
        [TestCase(0, .01f, 1)]
        [TestCase(0, 0, 2)]
        [TestCase(0, 0, 0)]
        public void IncompatibleTransforms_AreRejected(float angle, float offset, float scale)
        {
            var metadata = new VoxelBridgeMetadata { voxelSize = .125f, unityGridSize = Vector3Int.one * 2 };
            var matrix = Matrix4x4.TRS(Vector3.right * offset, Quaternion.Euler(0, angle, 0), new Vector3(scale, 1, 1));
            Assert.Throws<InvalidDataException>(() => new VoxelCellTransform(metadata, matrix, .125f));
        }

        [Test]
        public void UniformScale_IsAcceptedOnlyWhenPhysicalCellSizeMatches()
        {
            var metadata = new VoxelBridgeMetadata { voxelSize = .0625f, unityGridSize = Vector3Int.one * 2 };
            var cells = new VoxelCellTransform(metadata, Matrix4x4.Scale(Vector3.one * 2), .125f);
            Assert.That(cells.Map(1, 1, 1), Is.EqualTo(Vector3Int.one));
        }
    }

    public sealed class VoxelFamilyCombinerTests
    {
        private string folder, materialPath, sourcePath;
        private Scene preview;
        private GameObject parent, a, b;
        private VoxelStyleProfile profile;
        private VoxelColorPalette colors;
        private VoxelSurfacePalette surfaces;

        [SetUp]
        public void SetUp()
        {
            folder = "Assets/VoxelCombineTest_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder));
            preview = EditorSceneManager.NewPreviewScene();
            parent = new GameObject("Group"); SceneManager.MoveGameObjectToScene(parent, preview);
            profile = ScriptableObject.CreateInstance<VoxelStyleProfile>();
            colors = ScriptableObject.CreateInstance<VoxelColorPalette>();
            surfaces = ScriptableObject.CreateInstance<VoxelSurfacePalette>();
            AssetDatabase.CreateAsset(profile, folder + "/Profile.asset");
            AssetDatabase.CreateAsset(colors, folder + "/Colors.asset");
            AssetDatabase.CreateAsset(surfaces, folder + "/Surfaces.asset");
            var serialized = new SerializedObject(profile);
            serialized.FindProperty("baseVoxelSize").floatValue = .125f;
            serialized.FindProperty("chunkCellSize").intValue = 16;
            serialized.ApplyModifiedPropertiesWithoutUndo();
            Assert.That(VoxelPaletteLutGenerator.TryRebuild(colors, out _, out string error), Is.True, error);
            Assert.That(VoxelPaletteLutGenerator.TryRebuild(surfaces, out _, out error), Is.True, error);
            var mapping = ScriptableObject.CreateInstance<VoxelColorMappingProfile>();
            mapping.ConfigureForTests(colors, 100, 100);
            AssetDatabase.CreateAsset(mapping, folder + "/Mapping.asset");
            materialPath = VoxelProductionExporter.SharedMaterialFolder + "/VoxelWorld_" + Hash128.Compute(
                AssetDatabase.AssetPathToGUID(folder + "/Colors.asset") + ":" + AssetDatabase.AssetPathToGUID(folder + "/Surfaces.asset")) + ".mat";
            sourcePath = folder + "/Source.vox";
            WriteSource(sourcePath, Grid(), colors);
            string manifest = VoxelProductionFamily.Create(sourcePath, profile, folder + "/Input");
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(VoxelProductionFamily.Load(manifest).prefabAssetPath);
            a = (GameObject)PrefabUtility.InstantiatePrefab(prefab, preview); a.name = "First";
            b = (GameObject)PrefabUtility.InstantiatePrefab(prefab, preview); b.name = "Second";
            a.transform.SetParent(parent.transform, false); b.transform.SetParent(parent.transform, false);
            b.transform.localPosition = Vector3.right * 2.5f;
        }

        [TearDown]
        public void TearDown()
        {
            if (parent != null) Object.DestroyImmediate(parent);
            if (preview.IsValid()) EditorSceneManager.ClosePreviewScene(preview);
            if (materialPath != null) AssetDatabase.DeleteAsset(materialPath);
            if (folder != null) AssetDatabase.DeleteAsset(folder);
        }

        private static VoxelGrid Grid()
        {
            var grid = new VoxelGrid(new Vector3Int(20, 2, 2), Vector3.zero, .125f, true);
            for (int i = 0; i < grid.Occupied.Length; i++)
            { grid.Occupied[i] = true; grid.SemanticIds[i] = VoxelSemanticEncoding.Pack(54, i % 20 < 10 ? 7 : 13); }
            return grid;
        }

        private void WriteSource(string path, VoxelGrid grid, VoxelColorPalette palette, VoxelBridgeMetadata metadata = null)
        {
            var quantized = VoxelSemanticQuantizer.Quantize(grid, palette, surfaces);
            var write = VoxelChunkedVoxWriter.Write(VoxelLodPipeline.AssetPathToAbsolute(path), grid, quantized, 16);
            Assert.That(VoxelSemanticTransport.TryCreateMetadata(quantized.SemanticSlots, palette, surfaces, out var semantic, out string error), Is.True, error);
            metadata = metadata ?? new VoxelBridgeMetadata { sourceName = "Source", sourceBoundsMax = (Vector3)grid.Size * grid.VoxelSize };
            metadata.formatVersion = 4; metadata.voxelSize = grid.VoxelSize; metadata.baseVoxelSize = grid.VoxelSize;
            metadata.gridOrigin = grid.Origin; metadata.unityGridSize = grid.Size; metadata.chunks = write.Chunks;
            metadata.chunkCellSize = 16; metadata.semantic = semantic;
            metadata.semantic.colorMappingProfileGuid = AssetDatabase.AssetPathToGUID(folder + "/Mapping.asset");
            metadata.semantic.colorMappingProfileAssetPath = folder + "/Mapping.asset";
            string sidecar = VoxelImporterIntegration.GetMetadataAssetPath(path);
            File.WriteAllText(VoxelLodPipeline.AssetPathToAbsolute(sidecar), JsonUtility.ToJson(metadata, true));
            AssetDatabase.ImportAsset(sidecar, ImportAssetOptions.ForceSynchronousImport);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
        }

        private void ReplaceSecond(string path)
        {
            Object.DestroyImmediate(b);
            string manifest = VoxelProductionFamily.Create(path, profile, folder + "/Input");
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(VoxelProductionFamily.Load(manifest).prefabAssetPath);
            b = (GameObject)PrefabUtility.InstantiatePrefab(prefab, preview);
            b.transform.SetParent(parent.transform, false);
            b.transform.localPosition = Vector3.right * 2.5f;
        }

        [TestCase(1f)]
        [TestCase(2f)]
        public void Snap_PreviewsRootsPreservesScaleAndSupportsUndoRedo(float parentScale)
        {
            SceneManager.MoveGameObjectToScene(parent, SceneManager.GetActiveScene());
            parent.transform.SetPositionAndRotation(new Vector3(5, 1, -2), Quaternion.Euler(0, 31, 0));
            parent.transform.localScale = Vector3.one * parentScale;
            a.transform.localScale = Vector3.one / parentScale;
            b.transform.localScale = new Vector3(-1, 1, 1) / parentScale;
            a.transform.SetPositionAndRotation(new Vector3(-31.71f, 0, 71.736f), Quaternion.Euler(0, 182.8828f, 0));
            b.transform.SetPositionAndRotation(new Vector3(-30.61f, 0, 71.563f), Quaternion.Euler(0, 182.8828f, 0));
            var aPosition = a.transform.localPosition; var bPosition = b.transform.localPosition;
            var aRotation = a.transform.localRotation; var bRotation = b.transform.localRotation;
            var aScale = a.transform.localScale; var bScale = b.transform.localScale;
            var parentMatrix = parent.transform.localToWorldMatrix;
            byte[] sourceBytes = File.ReadAllBytes(VoxelLodPipeline.AssetPathToAbsolute(sourcePath));
            try
            {
                var plan = VoxelFamilyCombiner.PreviewSnap(new[] { a.transform.Find("LOD0").gameObject, b.transform.Find("LOD0").gameObject }, profile, true);
                Assert.That(plan.Select(p => p.Instance), Is.EquivalentTo(new[] { a, b }));
                Assert.That(plan.All(p => p.Changed), Is.True);
                Assert.That(a.transform.localPosition, Is.EqualTo(aPosition));
                Assert.That(b.transform.localRotation, Is.EqualTo(bRotation));
                Assert.That(VoxelFamilyCombiner.ApplySnap(plan, profile), Is.EqualTo(2));
                Assert.That(Quaternion.Angle(a.transform.rotation, Quaternion.Euler(0, 180, 0)), Is.LessThan(.01f));
                Assert.DoesNotThrow(() => VoxelFamilyCombiner.Analyze(new[] { parent }, profile));
                Assert.That(a.transform.localScale, Is.EqualTo(aScale));
                Assert.That(b.transform.localScale, Is.EqualTo(bScale));
                Assert.That(parent.transform.localToWorldMatrix, Is.EqualTo(parentMatrix));
                Assert.That(a.transform.Find("LOD0").localPosition, Is.EqualTo(Vector3.zero));
                Undo.PerformUndo();
                Assert.That(a.transform.localPosition, Is.EqualTo(aPosition));
                Assert.That(b.transform.localPosition, Is.EqualTo(bPosition));
                Assert.That(a.transform.localRotation, Is.EqualTo(aRotation));
                Assert.That(b.transform.localRotation, Is.EqualTo(bRotation));
                Undo.PerformRedo();
                Assert.That(a.transform.localPosition, Is.EqualTo(plan[0].LocalPosition));
                Assert.That(b.transform.localPosition, Is.EqualTo(plan[1].LocalPosition));
                Assert.That(b.transform.localRotation, Is.EqualTo(plan[1].LocalRotation));
                Assert.DoesNotThrow(() => VoxelFamilyCombiner.Analyze(new[] { parent }, profile));
                Assert.That(File.ReadAllBytes(VoxelLodPipeline.AssetPathToAbsolute(sourcePath)), Is.EqualTo(sourceBytes));
                Undo.PerformUndo();
            }
            finally { SceneManager.MoveGameObjectToScene(parent, preview); }
        }

        [Test]
        public void Snap_RejectsIncompatibleScaleWithoutChangingAnySource()
        {
            a.transform.position = new Vector3(.03f, .02f, .01f);
            b.transform.localScale = new Vector3(1.1f, 1, 1);
            var position = a.transform.position;
            int undoGroup = Undo.GetCurrentGroup();
            Assert.Throws<InvalidDataException>(() => VoxelFamilyCombiner.PreviewSnap(new[] { parent }, profile, true));
            Assert.That(a.transform.position, Is.EqualTo(position));
            Assert.That(Undo.GetCurrentGroup(), Is.EqualTo(undoGroup));
        }

        [Test]
        public void Snap_RejectsStalePreviewAndDoesNotRecordNoOp()
        {
            var aligned = VoxelFamilyCombiner.PreviewSnap(new[] { parent }, profile, true);
            int undoGroup = Undo.GetCurrentGroup();
            Assert.That(VoxelFamilyCombiner.ApplySnap(aligned, profile), Is.Zero);
            Assert.That(Undo.GetCurrentGroup(), Is.EqualTo(undoGroup));
            a.transform.position = new Vector3(.03f, .02f, .01f);
            var position = a.transform.position;
            var plan = VoxelFamilyCombiner.PreviewSnap(new[] { parent }, profile, true);
            b.transform.position += Vector3.up;
            Assert.Throws<InvalidOperationException>(() => VoxelFamilyCombiner.ApplySnap(plan, profile));
            Assert.That(a.transform.position, Is.EqualTo(position));
            Assert.That(Undo.GetCurrentGroup(), Is.EqualTo(undoGroup));
            plan = VoxelFamilyCombiner.PreviewSnap(new[] { parent }, profile, true);
            var otherScene = EditorSceneManager.NewPreviewScene();
            try
            {
                SceneManager.MoveGameObjectToScene(parent, otherScene);
                Assert.Throws<InvalidOperationException>(() => VoxelFamilyCombiner.ApplySnap(plan, profile));
            }
            finally { SceneManager.MoveGameObjectToScene(parent, preview); EditorSceneManager.ClosePreviewScene(otherScene); }
            var data = new SerializedObject(profile);
            data.FindProperty("baseVoxelSize").floatValue = .25f;
            data.ApplyModifiedPropertiesWithoutUndo();
            Assert.Throws<InvalidOperationException>(() => VoxelFamilyCombiner.ApplySnap(plan, profile));
            Assert.That(a.transform.position, Is.EqualTo(position));
        }

        [Test]
        public void RetainedGeometry_CopiesMeshesMaterialsAndReflectedPlacement()
        {
            var retained = new GameObject("Retained"); SceneManager.MoveGameObjectToScene(retained, preview);
            var window = GameObject.CreatePrimitive(PrimitiveType.Cube);
            SceneManager.MoveGameObjectToScene(window, preview); window.transform.SetParent(retained.transform, false);
            window.transform.localPosition = new Vector3(.25f, .5f, .75f);
            window.transform.localScale = Vector3.one * .125f;
            Object.DestroyImmediate(window.GetComponent<Collider>());
            var mesh = Object.Instantiate(window.GetComponent<MeshFilter>().sharedMesh);
            AssetDatabase.CreateAsset(mesh, folder + "/Window.asset"); window.GetComponent<MeshFilter>().sharedMesh = mesh;
            var material = new Material(Shader.Find("HDRP/Lit"));
            AssetDatabase.CreateAsset(material, folder + "/Window.mat"); window.GetComponent<MeshRenderer>().sharedMaterial = material;
            string retainedPath = folder + "/Retained.prefab";
            PrefabUtility.SaveAsPrefabAsset(retained, retainedPath);
            Object.DestroyImmediate(retained);
            string path = folder + "/WithWindow.vox";
            WriteSource(path, Grid(), colors, new VoxelBridgeMetadata { sourceName = "WithWindow", retainedGeometryGuid = AssetDatabase.AssetPathToGUID(retainedPath) });
            ReplaceSecond(path);
            b.transform.localRotation = Quaternion.Euler(0, 90, 0);
            b.transform.localScale = new Vector3(-1, 1, 1);
            var expected = b.transform.Find("LOD0/" + VoxelRetainedGeometry.ChildName).GetComponentInChildren<MeshRenderer>().bounds;
            var analysis = VoxelFamilyCombiner.Analyze(new[] { parent }, profile);
            var result = VoxelFamilyCombiner.Build(analysis, profile, folder + "/Output", "WithWindow", true, false);
            var manifest = VoxelProductionFamily.Load(result.ManifestAssetPath);
            Assert.That(manifest.retainedGeometryGuid, Is.Not.Empty);
            var snapshot = VoxelRetainedGeometry.Resolve(manifest.retainedGeometryGuid);
            Assert.That(snapshot.GetComponentInChildren<MeshFilter>().sharedMesh, Is.Not.SameAs(mesh));
            Assert.That(AssetDatabase.GetDependencies(result.PrefabAssetPath), Does.Not.Contain(folder + "/Window.asset"));
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(result.PrefabAssetPath);
            var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, preview);
            try
            {
                instance.transform.position = analysis.Pivot;
                foreach (Transform lod in instance.transform)
                {
                    var renderer = lod.Find(VoxelRetainedGeometry.ChildName).GetComponentInChildren<MeshRenderer>();
                    Assert.That(AssetDatabase.GetAssetPath(renderer.sharedMaterial), Is.EqualTo(folder + "/Window.mat"));
                    Assert.That(renderer.sharedMaterial == AssetDatabase.LoadAssetAtPath<Material>(folder + "/Window.mat"), Is.True);
                    Assert.That(Vector3.Distance(renderer.bounds.center, expected.center), Is.LessThan(.00001));
                    Assert.That(Vector3.Distance(renderer.bounds.size, expected.size), Is.LessThan(.00001));
                }
                Assert.That(instance.GetComponentInChildren<Collider>(), Is.Null);
            }
            finally { Object.DestroyImmediate(instance); }
        }

        [Test]
        public void PaletteValidation_RejectsDifferentLibrariesAndOldRevisions()
        {
            var other = Object.Instantiate(colors);
            AssetDatabase.CreateAsset(other, folder + "/OtherColors.asset");
            Assert.That(VoxelPaletteLutGenerator.TryRebuild(other, out _, out string error), Is.True, error);
            string path = folder + "/Other.vox";
            WriteSource(path, Grid(), other);
            ReplaceSecond(path);
            Assert.Throws<InvalidDataException>(() => VoxelFamilyCombiner.Analyze(new[] { parent }, profile));
            string secondMaterial = AssetDatabase.GetAssetPath(b.GetComponentInChildren<MeshRenderer>().sharedMaterial);
            try
            {
                WriteSource(path, Grid(), colors);
                Assert.That(VoxelImporterIntegration.TryLoadMetadata(path, out var metadata, out error), Is.True, error);
                metadata.semantic.colorPaletteHash = "outdated";
                File.WriteAllText(VoxelLodPipeline.AssetPathToAbsolute(VoxelImporterIntegration.GetMetadataAssetPath(path)), JsonUtility.ToJson(metadata, true));
                var failure = Assert.Throws<InvalidDataException>(() => VoxelFamilyCombiner.Analyze(new[] { parent }, profile));
                Assert.That(failure.Message, Does.Contain("older palette revision"));
            }
            finally { AssetDatabase.DeleteAsset(secondMaterial); }
        }

        [Test]
        public void CombinedPalette_RejectsMoreThan255PairsWithoutLosingIds()
        {
            int[] ids = colors.Entries.Take(128).Select(c => c.Id).ToArray();
            Assert.That(ids.Length, Is.EqualTo(128));
            var first = new VoxelGrid(new Vector3Int(128, 1, 1), Vector3.zero, .125f, true);
            var second = new VoxelGrid(first.Size, Vector3.zero, .125f, true);
            for (int i = 0; i < 128; i++)
            {
                first.Occupied[i] = second.Occupied[i] = true;
                first.SemanticIds[i] = VoxelSemanticEncoding.Pack(ids[i], 7);
                second.SemanticIds[i] = VoxelSemanticEncoding.Pack(ids[i], 13);
            }
            WriteSource(sourcePath, first, colors);
            string path = folder + "/Second.vox";
            WriteSource(path, second, colors); ReplaceSecond(path);
            b.transform.localPosition = Vector3.right * 16;
            var failure = Assert.Throws<InvalidDataException>(() => VoxelFamilyCombiner.Analyze(new[] { parent }, profile));
            Assert.That(failure.Message, Does.Contain("256 color/surface pairs"));
            Assert.That(AssetDatabase.IsValidFolder(folder + "/Output"), Is.False);
        }

        [Test]
        public void Selection_RejectsCrossSceneAndDisabledLevelOverrides()
        {
            var other = EditorSceneManager.NewPreviewScene();
            try
            {
                b.transform.SetParent(null); SceneManager.MoveGameObjectToScene(b, other);
                Assert.Throws<InvalidDataException>(() => VoxelFamilyCombiner.Collect(new[] { a, b }, true));
                SceneManager.MoveGameObjectToScene(b, preview); b.transform.SetParent(parent.transform, true);
                b.transform.Find("LOD0").gameObject.SetActive(false);
                Assert.Throws<InvalidDataException>(() => VoxelFamilyCombiner.Analyze(new[] { parent }, profile));
            }
            finally { EditorSceneManager.ClosePreviewScene(other); }
        }

        [TestCase(VoxelSourceAxes.PreserveLocalAxes)]
        [TestCase(VoxelSourceAxes.ZUp)]
        public void CombinedFamily_RoundTripsIdsChunksDerivedLodsAndStableRebuild(VoxelSourceAxes axes)
        {
            var input = VoxelProductionExporter.ReadGrid(sourcePath, out var inputMetadata, out _, out _, out _);
            inputMetadata.sourceAxes = axes;
            WriteSource(sourcePath, input, colors, inputMetadata);
            string inputManifestPath = AssetDatabase.GUIDToAssetPath(VoxelProductionLink.Load(
                PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(a)).manifestGuid);
            var inputManifest = VoxelProductionFamily.Load(inputManifestPath);
            inputManifest.sourceAxes = axes;
            VoxelLodPipeline.SaveManifest(inputManifestPath, inputManifest);
            string before = Hash128.Compute(File.ReadAllBytes(VoxelLodPipeline.AssetPathToAbsolute(sourcePath))).ToString();
            var analysis = VoxelFamilyCombiner.Analyze(new[] { parent }, profile);
            Assert.That(analysis.Grid.CountOccupied(), Is.EqualTo(160));
            Assert.That(analysis.Conflicts, Is.Zero);
            Assert.That(analysis.PairCount, Is.EqualTo(2));
            var result = VoxelFamilyCombiner.Build(analysis, profile, folder + "/Output", "Combined", true, false);
            var manifest = VoxelProductionFamily.Load(result.ManifestAssetPath);
            Assert.That(result.VoxAssetPaths.Length, Is.EqualTo(3));
            Assert.That(manifest.sourceAxes, Is.EqualTo(VoxelSourceAxes.PreserveLocalAxes));
            Assert.That(manifest.lods[0].meshGuids.Length, Is.EqualTo(3));
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(result.PrefabAssetPath);
            Assert.That(VoxelProductionLink.Load(result.PrefabAssetPath).manifestGuid, Is.EqualTo(AssetDatabase.AssetPathToGUID(result.ManifestAssetPath)));
            Assert.That(prefab.GetComponentsInChildren<LODGroup>(true).Length, Is.EqualTo(1));
            Assert.That(prefab.GetComponent<LODGroup>().fadeMode, Is.EqualTo(LODFadeMode.None));
            foreach (var entry in manifest.lods)
            {
                var grid = VoxelProductionExporter.ReadGrid(VoxelProductionFamily.SourcePath(entry), out var levelMetadata, out _, out _, out _);
                Assert.That(levelMetadata.semantic.colorMappingProfileGuid, Is.Null.Or.Empty);
                Assert.That(levelMetadata.sourceAxes, Is.EqualTo(VoxelSourceAxes.PreserveLocalAxes));
                var ids = grid.SemanticIds.Where((id, i) => grid.Occupied[i]).Distinct().ToArray();
                Assert.That(ids, Is.EquivalentTo(new[] { VoxelSemanticEncoding.Pack(54, 7), VoxelSemanticEncoding.Pack(54, 13) }));
                if (entry.lodIndex > 0)
                {
                    Assert.That(entry.generationMode, Is.EqualTo(VoxelLodGenerationMode.ReduceParent));
                    Assert.That(VoxelProductionFamily.IsDerivedStale(manifest, entry.lodIndex), Is.False);
                }
            }
            var source = VoxelProductionFamily.SourcePath(manifest.lods[0]);
            var edited = VoxelProductionExporter.ReadGrid(source, out var metadata, out _, out _, out _);
            edited.Occupied[0] = false;
            WriteSource(source, edited, colors, metadata);
            Assert.That(VoxelProductionFamily.IsDerivedStale(manifest, 1), Is.True);
            var guids = manifest.lods[0].meshGuids.ToArray();
            VoxelProductionFamily.RebuildLevel(result.ManifestAssetPath, 0);
            Assert.That(VoxelProductionFamily.Load(result.ManifestAssetPath).lods[0].meshGuids, Is.EqualTo(guids));
            Assert.That(Hash128.Compute(File.ReadAllBytes(VoxelLodPipeline.AssetPathToAbsolute(sourcePath))).ToString(), Is.EqualTo(before));
            Assert.That(a.activeSelf && b.activeSelf, Is.True);
        }

        [Test]
        public void Overlaps_FirstHierarchySourceWinsAndRequiresConfirmation()
        {
            b.transform.localPosition = Vector3.right * 1.25f;
            var analysis = VoxelFamilyCombiner.Analyze(new[] { b, a }, profile);
            Assert.That(analysis.Sources[0].Instance, Is.SameAs(a));
            Assert.That(analysis.Overlaps, Is.EqualTo(40));
            Assert.That(analysis.Conflicts, Is.EqualTo(40));
            Assert.That(analysis.Grid.SemanticIds[analysis.Grid.Index(10, 0, 0)], Is.EqualTo(VoxelSemanticEncoding.Pack(54, 13)));
            Assert.Throws<InvalidOperationException>(() => VoxelFamilyCombiner.Build(analysis, profile, folder + "/Output", "Conflict", false, false));
            Assert.That(AssetDatabase.IsValidFolder(folder + "/Output"), Is.False);
            var result = VoxelFamilyCombiner.Build(analysis, profile, folder + "/Output", "Accepted", false, true);
            Assert.That(result.VoxAssetPaths.Length, Is.EqualTo(1));
        }

        [Test]
        public void IdenticalOverlaps_DeduplicateWithoutConflict()
        {
            b.transform.localPosition = Vector3.zero;
            var analysis = VoxelFamilyCombiner.Analyze(new[] { parent, a, b }, profile);
            Assert.That(analysis.Sources.Length, Is.EqualTo(2));
            Assert.That(analysis.Grid.CountOccupied(), Is.EqualTo(80));
            Assert.That(analysis.Overlaps, Is.EqualTo(80));
            Assert.That(analysis.Conflicts, Is.Zero);
        }

        [Test]
        public void Selection_FiltersInactiveRejectsUnknownAndPreservesHierarchy()
        {
            b.SetActive(false);
            Assert.Throws<InvalidDataException>(() => VoxelFamilyCombiner.Collect(new[] { parent }, true));
            Assert.That(VoxelFamilyCombiner.Collect(new[] { parent }, false).Length, Is.EqualTo(2));
            var unknown = new GameObject("Unknown", typeof(MeshRenderer));
            SceneManager.MoveGameObjectToScene(unknown, preview); unknown.transform.SetParent(parent.transform);
            Assert.Throws<InvalidDataException>(() => VoxelFamilyCombiner.Collect(new[] { parent }, false));
            Assert.That(parent.transform.childCount, Is.EqualTo(3));
        }

        [Test]
        public void Analysis_RejectsOffGridBoundsAndChildOverrides()
        {
            b.transform.localPosition = Vector3.one * .01f;
            Assert.Throws<InvalidDataException>(() => VoxelFamilyCombiner.Analyze(new[] { parent }, profile));
            b.transform.localPosition = Vector3.one * 250;
            Assert.Throws<InvalidDataException>(() => VoxelFamilyCombiner.Analyze(new[] { parent }, profile));
            b.transform.localPosition = Vector3.right * 2.5f;
            b.transform.Find("LOD0").localPosition = Vector3.up;
            Assert.Throws<InvalidDataException>(() => VoxelFamilyCombiner.Analyze(new[] { parent }, profile));
        }

        [Test]
        public void Analysis_RejectsExtraMeshesEvenWhenSavedInSourcePrefab()
        {
            string path = VoxelProductionLink.PrefabPath(a);
            var contents = PrefabUtility.LoadPrefabContents(path);
            try
            {
                var source = contents.GetComponentInChildren<MeshFilter>();
                var extra = new GameObject("Extra", typeof(MeshFilter), typeof(MeshRenderer));
                extra.transform.SetParent(contents.transform.Find("LOD0"), false);
                extra.GetComponent<MeshFilter>().sharedMesh = source.sharedMesh;
                extra.GetComponent<MeshRenderer>().sharedMaterial = source.GetComponent<MeshRenderer>().sharedMaterial;
                PrefabUtility.SaveAsPrefabAsset(contents, path);
            }
            finally { PrefabUtility.UnloadPrefabContents(contents); }
            Assert.Throws<InvalidDataException>(() => VoxelFamilyCombiner.Analyze(new[] { parent }, profile));
        }

        [Test]
        public void CancelledBuild_RemovesOnlyNewFamily()
        {
            var analysis = VoxelFamilyCombiner.Analyze(new[] { parent }, profile);
            VoxelLodPipeline.EnsureAssetFolder(folder + "/Output");
            int calls = 0;
            Assert.Throws<OperationCanceledException>(() => VoxelFamilyCombiner.Build(analysis, profile,
                folder + "/Output", "Cancelled", true, false, _ => { if (++calls > 3) throw new OperationCanceledException(); }));
            Assert.That(calls, Is.GreaterThan(3));
            Assert.That(AssetDatabase.GetSubFolders(folder + "/Output"), Is.Empty);
            Assert.That(File.Exists(VoxelLodPipeline.AssetPathToAbsolute(sourcePath)), Is.True);
            Assert.That(a.activeSelf && b.activeSelf, Is.True);
        }

        [Test]
        public void Placement_PreservesWorldGeometryAndUndoRedo()
        {
            // Test runner supplies a temporary normal scene; source fixtures otherwise use preview scenes.
            SceneManager.MoveGameObjectToScene(parent, SceneManager.GetActiveScene());
            parent.transform.position = new Vector3(5, 1, -2);
            var analysis = VoxelFamilyCombiner.Analyze(new[] { parent }, profile);
            var result = VoxelFamilyCombiner.Build(analysis, profile, folder + "/Output", "Placed", false, false);
            GameObject placed = null;
            try
            {
                placed = VoxelFamilyCombiner.Place(analysis, result, true);
                Assert.That(placed.transform.position, Is.EqualTo(parent.transform.position));
                Assert.That(placed.transform.rotation, Is.EqualTo(Quaternion.identity));
                Assert.That(a.activeSelf || b.activeSelf, Is.False);
                Assert.That(parent.activeSelf, Is.True);
                Undo.FlushUndoRecordObjects(); Undo.PerformUndo();
                Assert.That(a.activeSelf && b.activeSelf, Is.True);
                Assert.That(placed == null, Is.True);
                Undo.PerformRedo();
                Assert.That(a.activeSelf || b.activeSelf, Is.False);
                Undo.PerformUndo();
            }
            finally
            {
                if (placed != null) Object.DestroyImmediate(placed);
                SceneManager.MoveGameObjectToScene(parent, preview);
            }
        }
    }
}
