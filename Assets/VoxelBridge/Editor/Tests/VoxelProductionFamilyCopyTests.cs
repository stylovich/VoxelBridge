using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LocalModels.VoxelBridge.Tests
{
    public sealed class VoxelProductionFamilyCopyTests
    {
        private string folder, source, manifestPath, materialPath;
        private VoxelColorPalette colors;
        private VoxelSurfacePalette surfaces;

        [SetUp]
        public void SetUp()
        {
            folder = "Assets/FamilyCopyTest_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder));
            colors = ScriptableObject.CreateInstance<VoxelColorPalette>();
            surfaces = ScriptableObject.CreateInstance<VoxelSurfacePalette>();
            var profile = ScriptableObject.CreateInstance<VoxelStyleProfile>();
            AssetDatabase.CreateAsset(colors, folder + "/Colors.asset");
            AssetDatabase.CreateAsset(surfaces, folder + "/Surfaces.asset");
            AssetDatabase.CreateAsset(profile, folder + "/Profile.asset");
            using (var settings = new SerializedObject(profile))
            { settings.FindProperty("chunkCellSize").intValue = 16; settings.ApplyModifiedPropertiesWithoutUndo(); }
            Assert.That(VoxelPaletteLutGenerator.TryRebuild(colors, out _, out string error), Is.True, error);
            Assert.That(VoxelPaletteLutGenerator.TryRebuild(surfaces, out _, out error), Is.True, error);
            var grid = new VoxelGrid(new Vector3Int(20, 3, 2), Vector3.zero, .032f, true);
            for (int i = 0; i < grid.Occupied.Length; i++)
            { grid.Occupied[i] = true; grid.SemanticIds[i] = VoxelSemanticEncoding.Pack(54, i < 20 ? 13 : 1); }
            source = folder + "/Source.vox";
            var quantized = VoxelSemanticQuantizer.Quantize(grid, colors, surfaces);
            var write = VoxelChunkedVoxWriter.Write(VoxelLodPipeline.AssetPathToAbsolute(source), grid, quantized, 16);
            Assert.That(VoxelSemanticTransport.TryCreateMetadata(quantized.SemanticSlots, colors, surfaces, out var semantic, out error), Is.True, error);
            var metadata = new VoxelBridgeMetadata { formatVersion = 4, sourceName = "Source", voxelSize = grid.VoxelSize,
                baseVoxelSize = grid.VoxelSize, gridOrigin = grid.Origin, unityGridSize = grid.Size, chunks = write.Chunks,
                sourceBoundsMax = (Vector3)grid.Size * grid.VoxelSize, chunkCellSize = 16, semantic = semantic };
            WriteMetadata(source, metadata);
            manifestPath = VoxelProductionFamily.Create(source, profile, folder);
            var original = VoxelProductionFamily.Load(manifestPath);
            materialPath = AssetDatabase.GetAssetPath(AssetDatabase.LoadAssetAtPath<GameObject>(original.prefabAssetPath)
                .GetComponentInChildren<MeshRenderer>().sharedMaterial);
        }

        [TearDown]
        public void TearDown()
        {
            if (folder != null) AssetDatabase.DeleteAsset(folder);
            if (materialPath != null) AssetDatabase.DeleteAsset(materialPath);
        }

        [Test]
        public void Duplicate_CopiesAllLodsAndSavedPrefab_EditingEitherFamilyIsIndependent()
        {
            VoxelProductionFamily.DeriveLevel(manifestPath, 1, VoxelLodGenerationMode.DuplicateParent);
            VoxelProductionFamily.DeriveLevel(manifestPath, 2, VoxelLodGenerationMode.ReduceParent);
            var original = VoxelProductionFamily.Load(manifestPath);
            var root = PrefabUtility.LoadPrefabContents(original.prefabAssetPath);
            try
            {
                root.AddComponent<BoxCollider>().size = new Vector3(2, 3, 4);
                root.transform.localScale = new Vector3(1, 2, 3);
                var group = root.GetComponent<LODGroup>(); var lods = group.GetLODs();
                lods[0].screenRelativeTransitionHeight = .82f; group.SetLODs(lods);
                PrefabUtility.SaveAsPrefabAsset(root, original.prefabAssetPath);
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
            var before = Snapshot();
            string copy = VoxelProductionFamilyCopy.Duplicate(original.prefabAssetPath, folder);
            AssertOriginalUnchanged(before);
            var cloned = ReadCopy(copy);
            Assert.That(cloned.familyId, Is.Not.EqualTo(original.familyId));
            Assert.That(cloned.profileGuid, Is.EqualTo(original.profileGuid));
            Assert.That(cloned.lods.Length, Is.EqualTo(3));
            var originalPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(original.prefabAssetPath);
            var copyPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(copy);
            Assert.That(PrefabUtility.GetPrefabAssetType(copyPrefab), Is.EqualTo(PrefabAssetType.Regular));
            Assert.That(AssetDatabase.GetDependencies(copy).Intersect(
                original.lods.SelectMany(l => l.meshGuids).Select(AssetDatabase.GUIDToAssetPath)), Is.Empty);
            Assert.That(copyPrefab.GetComponent<BoxCollider>().size, Is.EqualTo(new Vector3(2, 3, 4)));
            Assert.That(copyPrefab.transform.localScale, Is.EqualTo(new Vector3(1, 2, 3)));
            Assert.That(copyPrefab.GetComponent<LODGroup>().GetLODs()[0].screenRelativeTransitionHeight, Is.EqualTo(.82f));
            Assert.That(copyPrefab.GetComponentInChildren<MeshRenderer>().sharedMaterial,
                Is.SameAs(originalPrefab.GetComponentInChildren<MeshRenderer>().sharedMaterial));
            for (int i = 0; i < 3; i++)
            {
                string a = VoxelProductionFamily.SourcePath(original.lods[i]), b = VoxelProductionFamily.SourcePath(cloned.lods[i]);
                Assert.That(File.ReadAllBytes(a), Is.EqualTo(File.ReadAllBytes(b)));
                Assert.That(cloned.lods[i].sourceGuid, Is.Not.EqualTo(original.lods[i].sourceGuid));
                Assert.That(cloned.lods[i].meshGuids.Intersect(original.lods[i].meshGuids), Is.Empty);
                var aGrid = VoxelProductionExporter.ReadGrid(a, out _, out _, out _, out _);
                var bGrid = VoxelProductionExporter.ReadGrid(b, out var meta, out _, out _, out _);
                Assert.That(bGrid.SemanticIds, Is.EqualTo(aGrid.SemanticIds));
                Assert.That(meta.familyId, Is.EqualTo(cloned.familyId));
                Assert.That(meta.lodSetAssetPath, Is.EqualTo(AssetDatabase.GUIDToAssetPath(VoxelProductionLink.Load(copy).manifestGuid)));
                Assert.That(meta.parentVoxAssetPath, Is.EqualTo(i == 0 ? "" : cloned.lods[i - 1].voxAssetPath));
                Assert.That(cloned.lods[i].builtSourceHash, Is.EqualTo(VoxelProductionFamily.SourceHash(cloned.lods[i])));
                Assert.That(VoxelProductionFamily.IsDerivedStale(cloned, i), Is.False);
            }
            string copyManifest = AssetDatabase.GUIDToAssetPath(VoxelProductionLink.Load(copy).manifestGuid);
            var edit = new VoxelSurfaceEdit(cloned.lods[0].voxAssetPath);
            edit.Apply(new[] { 0 }, 7); edit.ApplyColor(new[] { 0 }, 55);
            Assert.That(VoxelSurfaceEditStore.Save(cloned.lods[0].voxAssetPath, edit), Is.Null);
            VoxelProductionFamily.RebuildLevel(copyManifest, 0);
            AssertOriginalUnchanged(before);
            Assert.That(new VoxelSurfaceEdit(source).Grid.SemanticIds[0], Is.EqualTo(VoxelSemanticEncoding.Pack(54, 13)));
            var copyBytes = File.ReadAllBytes(cloned.lods[0].voxAssetPath);
            var originalEdit = new VoxelSurfaceEdit(source); originalEdit.Apply(new[] { 1 }, 6);
            VoxelSurfaceEditStore.Save(source, originalEdit); VoxelProductionFamily.RebuildLevel(manifestPath, 0);
            Assert.That(File.ReadAllBytes(cloned.lods[0].voxAssetPath), Is.EqualTo(copyBytes));
            string second = VoxelProductionFamilyCopy.Duplicate(original.prefabAssetPath, folder);
            Assert.That(second, Is.Not.EqualTo(copy));
            Assert.That(VoxelProductionLink.Load(second).sourceGuid, Is.Not.EqualTo(VoxelProductionLink.Load(copy).sourceGuid));
        }

        [Test]
        public void Duplicate_PreservesPendingMeshAndDescendantWarnings()
        {
            VoxelProductionFamily.DeriveLevel(manifestPath, 1, VoxelLodGenerationMode.DuplicateParent);
            var edit = new VoxelSurfaceEdit(source); edit.Apply(new[] { 0 }, 7); VoxelSurfaceEditStore.Save(source, edit);
            var original = VoxelProductionFamily.Load(manifestPath);
            var before = Snapshot();
            var copy = ReadCopy(VoxelProductionFamilyCopy.Duplicate(original.prefabAssetPath, folder));
            Assert.That(copy.lods[0].builtSourceHash, Is.Not.EqualTo(VoxelProductionFamily.SourceHash(copy.lods[0])));
            Assert.That(VoxelProductionFamily.IsDerivedStale(copy, 1), Is.True);
            AssertOriginalUnchanged(before);
        }

        [Test]
        public void Duplicate_CopiesRetainedSnapshotMeshesAndColliderReferences()
        {
            var primitive = GameObject.CreatePrimitive(PrimitiveType.Cube);
            string retainedPath = folder + "/Retained.prefab";
            try
            {
                var mesh = Object.Instantiate(primitive.GetComponent<MeshFilter>().sharedMesh);
                AssetDatabase.CreateAsset(mesh, folder + "/RetainedMesh.asset");
                primitive.GetComponent<MeshFilter>().sharedMesh = mesh;
                Object.DestroyImmediate(primitive.GetComponent<BoxCollider>());
                primitive.AddComponent<MeshCollider>().sharedMesh = mesh;
                PrefabUtility.SaveAsPrefabAsset(primitive, retainedPath);
            }
            finally { Object.DestroyImmediate(primitive); }
            var original = VoxelProductionFamily.Load(manifestPath);
            original.retainedGeometryGuid = AssetDatabase.AssetPathToGUID(retainedPath);
            VoxelLodPipeline.SaveManifest(manifestPath, original);
            Assert.That(VoxelImporterIntegration.TryLoadMetadata(source, out var meta, out string error), Is.True, error);
            meta.retainedGeometryGuid = original.retainedGeometryGuid;
            WriteMetadata(source, meta);
            VoxelProductionFamily.RebuildLevel(manifestPath, 0);
            var before = Snapshot();
            string path = VoxelProductionFamilyCopy.Duplicate(original.prefabAssetPath, folder);
            var copy = ReadCopy(path);
            Assert.That(copy.retainedGeometryGuid, Is.Not.EqualTo(original.retainedGeometryGuid));
            var snapshot = VoxelRetainedGeometry.Resolve(copy.retainedGeometryGuid);
            var originalMesh = VoxelRetainedGeometry.Resolve(original.retainedGeometryGuid).GetComponent<MeshFilter>().sharedMesh;
            var copyMesh = snapshot.GetComponent<MeshFilter>().sharedMesh;
            Assert.That(copyMesh, Is.Not.SameAs(originalMesh));
            Assert.That(copyMesh.vertices, Is.EqualTo(originalMesh.vertices));
            Assert.That(snapshot.GetComponent<MeshCollider>().sharedMesh, Is.SameAs(copyMesh));
            var copyPrefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            var retained = copyPrefab.transform.Find("LOD0/" + VoxelRetainedGeometry.ChildName);
            Assert.That(retained.GetComponent<MeshFilter>().sharedMesh, Is.SameAs(copyMesh));
            Assert.That(retained.GetComponent<MeshCollider>().sharedMesh, Is.SameAs(copyMesh));
            VoxelProductionFamily.RebuildLevel(AssetDatabase.GUIDToAssetPath(VoxelProductionLink.Load(path).manifestGuid), 0);
            AssertOriginalUnchanged(before);
        }

        [Test]
        public void Duplicate_RejectsConcurrentSourceChangeAndRemovesIncompleteCopy()
        {
            var original = VoxelProductionFamily.Load(manifestPath);
            string sidecar = VoxelImporterIntegration.GetMetadataAssetPath(source);
            string previous = File.ReadAllText(sidecar);
            var paths = Directory.GetFiles(folder, "*", SearchOption.AllDirectories).OrderBy(p => p).ToArray();
            Assert.Throws<IOException>(() => VoxelProductionFamilyCopy.Duplicate(original.prefabAssetPath, folder,
                value => { if (value >= .95f) File.AppendAllText(sidecar, " "); }));
            Assert.That(File.ReadAllText(sidecar), Is.EqualTo(previous + " "), "External edits must not be rolled back.");
            Assert.That(Directory.GetFiles(folder, "*", SearchOption.AllDirectories).OrderBy(p => p), Is.EqualTo(paths));
        }

        [TestCase(.2f)]
        [TestCase(.7f)]
        [TestCase(.95f)]
        public void Duplicate_CancellationRemovesOnlyIncompleteCopy(float cancelAt)
        {
            var original = VoxelProductionFamily.Load(manifestPath);
            var before = Snapshot();
            Assert.Throws<OperationCanceledException>(() => VoxelProductionFamilyCopy.Duplicate(original.prefabAssetPath, folder,
                value => { if (value >= cancelAt) throw new OperationCanceledException(); }));
            AssertOriginalUnchanged(before);
            Assert.That(Directory.GetFiles(folder, "*", SearchOption.AllDirectories).OrderBy(p => p), Is.EqualTo(before.Keys.OrderBy(p => p)));
        }

        [Test]
        public void Duplicate_RejectsCopiedLinkAndMissingSourceWithoutCreatingOutput()
        {
            var original = VoxelProductionFamily.Load(manifestPath);
            string bad = folder + "/BadCopy.prefab";
            Assert.That(AssetDatabase.CopyAsset(original.prefabAssetPath, bad), Is.True);
            Assert.Throws<InvalidDataException>(() => VoxelProductionFamilyCopy.Duplicate(bad, folder));
            AssetDatabase.DeleteAsset(VoxelImporterIntegration.GetMetadataAssetPath(source));
            var before = Snapshot();
            Assert.Throws<InvalidDataException>(() => VoxelProductionFamilyCopy.Duplicate(original.prefabAssetPath, folder));
            AssertOriginalUnchanged(before);
            Assert.That(Directory.GetFiles(folder, "*", SearchOption.AllDirectories).OrderBy(p => p), Is.EqualTo(before.Keys.OrderBy(p => p)));
        }

        [Test]
        public void Duplicate_RejectsInvalidDestinationAndImpostor()
        {
            var original = VoxelProductionFamily.Load(manifestPath);
            Assert.Throws<ArgumentException>(() => VoxelProductionFamilyCopy.Duplicate(original.prefabAssetPath, "Assets/../Library"));
            original.impostor = new VoxelImpostorEntry { assetPath = "Assets/not-supported.asset" };
            VoxelLodPipeline.SaveManifest(manifestPath, original);
            Assert.Throws<InvalidOperationException>(() => VoxelProductionFamilyCopy.Duplicate(original.prefabAssetPath, folder));
        }

        [Test]
        public void Duplicate_RejectsMissingChunkLinksAndNestedPrefabs()
        {
            var original = VoxelProductionFamily.Load(manifestPath);
            string[] meshes = original.lods[0].meshGuids;
            original.lods[0].meshGuids = null;
            VoxelLodPipeline.SaveManifest(manifestPath, original);
            Assert.Throws<InvalidDataException>(() => VoxelProductionFamilyCopy.Duplicate(original.prefabAssetPath, folder));
            original.lods[0].meshGuids = meshes;
            VoxelLodPipeline.SaveManifest(manifestPath, original);
            var child = new GameObject("Nested");
            GameObject nested;
            try { nested = PrefabUtility.SaveAsPrefabAsset(child, folder + "/Nested.prefab"); }
            finally { Object.DestroyImmediate(child); }
            var root = PrefabUtility.LoadPrefabContents(original.prefabAssetPath);
            try
            {
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(nested, root.transform);
                PrefabUtility.SaveAsPrefabAsset(root, original.prefabAssetPath);
            }
            finally { PrefabUtility.UnloadPrefabContents(root); }
            var before = Snapshot();
            Assert.Throws<InvalidDataException>(() => VoxelProductionFamilyCopy.Duplicate(original.prefabAssetPath, folder));
            AssertOriginalUnchanged(before);
        }

        private static VoxelLodSetManifest ReadCopy(string copy) =>
            VoxelProductionFamily.Load(AssetDatabase.GUIDToAssetPath(VoxelProductionLink.Load(copy).manifestGuid));

        private Dictionary<string, byte[]> Snapshot() => Directory.GetFiles(folder, "*", SearchOption.AllDirectories)
            .ToDictionary(path => path, File.ReadAllBytes);

        private static void AssertOriginalUnchanged(Dictionary<string, byte[]> before)
        {
            foreach (var pair in before) Assert.That(File.ReadAllBytes(pair.Key), Is.EqualTo(pair.Value), pair.Key);
        }

        private static void WriteMetadata(string path, VoxelBridgeMetadata metadata)
        {
            string sidecar = VoxelImporterIntegration.GetMetadataAssetPath(path);
            File.WriteAllText(sidecar, JsonUtility.ToJson(metadata, true));
            AssetDatabase.ImportAsset(sidecar, ImportAssetOptions.ForceSynchronousImport);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
        }
    }
}
