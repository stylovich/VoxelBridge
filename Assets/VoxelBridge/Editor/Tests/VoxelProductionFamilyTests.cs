using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace LocalModels.VoxelBridge.Tests
{
    public sealed class VoxelProductionFamilyTests
    {
        [TestCase(true)]
        [TestCase(false)]
        public void ChunkMeshing_MatchesWholeVolumeAndRemovesBoundaryFaces(bool hideCavities)
        {
            var grid = new VoxelGrid(new Vector3Int(20, 3, 3), new Vector3(-1, 2, 3), 0.032f, true);
            for (int i = 0; i < grid.Occupied.Length; i++)
            { grid.Occupied[i] = true; grid.SemanticIds[i] = VoxelSemanticEncoding.Pack(54, 1); }
            grid.Occupied[grid.Index(15, 1, 1)] = false;
            grid.Occupied[grid.Index(16, 1, 1)] = false;
            Mesh whole = VoxelSemanticMesher.Build(grid, hideCavities);
            Mesh[] chunks = VoxelSemanticMesher.BuildChunks(grid, 16, hideCavities);
            try
            {
                Assert.That(chunks.Length, Is.EqualTo(2));
                Assert.That(chunks.Sum(Area), Is.EqualTo(Area(whole)).Within(0.00001f));
                foreach (Mesh mesh in chunks)
                {
                    Assert.That(mesh.uv.All(uv => uv.x == 54), Is.True);
                    Assert.That(mesh.uv4.All(uv => uv.x == 1), Is.True);
                    var vertices = mesh.vertices; var normals = mesh.normals;
                    for (int i = 0; i < vertices.Length; i++)
                        if (Mathf.Abs(vertices[i].x - (grid.Origin.x + 16 * grid.VoxelSize)) < 0.000001f)
                            Assert.That(Mathf.Abs(normals[i].x), Is.LessThan(0.5f), "A chunk boundary emitted an internal face.");
                }
            }
            finally { Object.DestroyImmediate(whole); foreach (Mesh mesh in chunks) Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void ChunkMeshing_KeepsEmptyPartitionsStable()
        {
            var grid = new VoxelGrid(new Vector3Int(20, 1, 1), Vector3.zero, 0.032f, true);
            grid.Occupied[0] = true;
            Mesh[] chunks = VoxelSemanticMesher.BuildChunks(grid, 16, true);
            try { Assert.That(chunks.Length, Is.EqualTo(2)); Assert.That(chunks[1].vertexCount, Is.Zero); }
            finally { foreach (Mesh mesh in chunks) Object.DestroyImmediate(mesh); }
        }

        private static float Area(Mesh mesh)
        {
            var vertices = mesh.vertices; var triangles = mesh.triangles; float area = 0;
            for (int i = 0; i < triangles.Length; i += 3)
                area += Vector3.Cross(vertices[triangles[i + 1]] - vertices[triangles[i]],
                    vertices[triangles[i + 2]] - vertices[triangles[i]]).magnitude * 0.5f;
            return area;
        }

        [Test]
        public void ProductionFamily_PreservesIdsSourcesChunksReferencesAndAuthoredDescendants()
        {
            string folder = "Assets/VoxelFamilyTest_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder));
            string sharedMaterialPath = null; GameObject instance = null;
            try
            {
                var profile = ScriptableObject.CreateInstance<VoxelStyleProfile>();
                AssetDatabase.CreateAsset(profile, folder + "/Profile.asset");
                var serialized = new SerializedObject(profile);
                serialized.FindProperty("chunkCellSize").intValue = 16;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                var colors = ScriptableObject.CreateInstance<VoxelColorPalette>();
                var surfaces = ScriptableObject.CreateInstance<VoxelSurfacePalette>();
                AssetDatabase.CreateAsset(colors, folder + "/Colors.asset");
                AssetDatabase.CreateAsset(surfaces, folder + "/Surfaces.asset");
                Assert.That(VoxelPaletteLutGenerator.TryRebuild(colors, out _, out string error), Is.True, error);
                Assert.That(VoxelPaletteLutGenerator.TryRebuild(surfaces, out _, out error), Is.True, error);
                var grid = new VoxelGrid(new Vector3Int(20, 3, 2), Vector3.zero, 0.032f, true);
                for (int i = 0; i < grid.Occupied.Length; i++)
                { grid.Occupied[i] = true; grid.SemanticIds[i] = VoxelSemanticEncoding.Pack(54, 1); }
                string source = folder + "/Source.vox";
                WriteSource(source, grid, colors, surfaces);
                string sourceHash = FileHash(source);
                string manifestPath = VoxelProductionFamily.Create(source, profile, folder + "/Output");
                var manifest = VoxelProductionFamily.Load(manifestPath);
                Assert.That(manifest.lods.Length, Is.EqualTo(1));
                Assert.That(manifest.baseVoxelSize, Is.EqualTo(grid.VoxelSize));
                Assert.That(manifest.lods[0].meshGuids.Length, Is.EqualTo(2));
                Assert.That(VoxelProductionFamily.Create(source, profile, folder + "/Output"), Is.EqualTo(manifestPath));
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(manifest.prefabAssetPath);
                var material = prefab.GetComponentInChildren<MeshRenderer>().sharedMaterial;
                sharedMaterialPath = AssetDatabase.GetAssetPath(material);
                instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                instance.transform.position = new Vector3(7, 8, 9);
                PrefabUtility.RecordPrefabInstancePropertyModifications(instance.transform);
                instance.AddComponent<BoxCollider>();
                string[] meshGuids = manifest.lods[0].meshGuids.ToArray();
                VoxelProductionFamily.DeriveLevel(manifestPath, 1, VoxelLodGenerationMode.DuplicateParent);
                VoxelProductionFamily.DeriveLevel(manifestPath, 2, VoxelLodGenerationMode.ReduceParent);
                manifest = VoxelProductionFamily.Load(manifestPath);
                Assert.That(manifest.lods[1].multiplier, Is.EqualTo(1));
                Assert.That(manifest.lods[2].multiplier, Is.EqualTo(4));
                Assert.That(FileHash(source), Is.EqualTo(sourceHash));
                Assert.That(FileHash(VoxelProductionFamily.SourcePath(manifest.lods[1])), Is.EqualTo(sourceHash));
                foreach (var entry in manifest.lods)
                {
                    var volume = VoxelProductionExporter.ReadGrid(VoxelProductionFamily.SourcePath(entry), out _, out _, out _, out _);
                    Assert.That(Enumerable.Range(0, volume.Occupied.Length).Where(i => volume.Occupied[i])
                        .All(i => volume.SemanticIds[i] == VoxelSemanticEncoding.Pack(54, 1)), Is.True);
                }
                Assert.That(prefab.GetComponent<LODGroup>().fadeMode, Is.EqualTo(LODFadeMode.None));
                Assert.That(prefab.GetComponent<LODGroup>().GetLODs().Length, Is.EqualTo(3));
                Assert.That(prefab.GetComponentsInChildren<MeshRenderer>().All(r => r.sharedMaterial == material), Is.True);
                Assert.That(prefab.GetComponent<LODGroup>().GetLODs()[2].renderers.All(r => r.shadowCastingMode == ShadowCastingMode.Off), Is.True);
                Assert.That(AssetDatabase.GetDependencies(manifest.prefabAssetPath).Any(p => p.EndsWith(".vox") || p.Contains("VoxelImporter")), Is.False);
                string descendantPath = VoxelProductionFamily.SourcePath(manifest.lods[2]);
                string descendantHash = FileHash(descendantPath);
                grid.Occupied[0] = false;
                WriteSource(source, grid, colors, surfaces);
                Assert.That(VoxelProductionFamily.IsDerivedStale(manifest, 1), Is.True);
                Assert.That(VoxelProductionFamily.IsDerivedStale(manifest, 2), Is.True);
                VoxelProductionFamily.RebuildLevel(manifestPath, 0);
                manifest = VoxelProductionFamily.Load(manifestPath);
                Assert.That(manifest.lods[0].meshGuids, Is.EqualTo(meshGuids));
                Assert.That(manifest.lods[0].builtSourceHash, Is.EqualTo(VoxelProductionFamily.SourceHash(manifest.lods[0])));
                Assert.That(FileHash(descendantPath), Is.EqualTo(descendantHash));
                Assert.That(VoxelProductionFamily.IsDerivedStale(manifest, 2), Is.True);
                Assert.That(instance.transform.position, Is.EqualTo(new Vector3(7, 8, 9)));
                Assert.That(instance.GetComponent<BoxCollider>(), Is.Not.Null);
                Assert.Throws<InvalidOperationException>(() => VoxelProductionFamily.DeriveLevel(manifestPath, 1, VoxelLodGenerationMode.ReduceParent));
                string parentGuid = manifest.lods[1].sourceGuid;
                VoxelProductionFamily.DeriveLevel(manifestPath, 1, VoxelLodGenerationMode.ReduceParent, true);
                manifest = VoxelProductionFamily.Load(manifestPath);
                Assert.That(manifest.lods[1].sourceGuid, Is.EqualTo(parentGuid));
                Assert.That(manifest.lods[1].multiplier, Is.EqualTo(2));
                Assert.That(VoxelProductionFamily.IsDerivedStale(manifest, 1), Is.False);
                Assert.That(VoxelProductionFamily.IsDerivedStale(manifest, 2), Is.True);
                Assert.That(FileHash(descendantPath), Is.EqualTo(descendantHash));
                string beforeCancel = FileHash(VoxelProductionFamily.SourcePath(manifest.lods[1]));
                string manifestBefore = File.ReadAllText(VoxelLodPipeline.AssetPathToAbsolute(manifestPath));
                Assert.Throws<OperationCanceledException>(() => VoxelProductionFamily.DeriveLevel(manifestPath, 1,
                    VoxelLodGenerationMode.DuplicateParent, true, _ => throw new OperationCanceledException()));
                Assert.That(FileHash(VoxelProductionFamily.SourcePath(manifest.lods[1])), Is.EqualTo(beforeCancel));
                Assert.That(File.ReadAllText(VoxelLodPipeline.AssetPathToAbsolute(manifestPath)), Is.EqualTo(manifestBefore));
                int progressCalls = 0;
                Assert.Throws<OperationCanceledException>(() => VoxelProductionFamily.DeriveLevel(manifestPath, 1,
                    VoxelLodGenerationMode.DuplicateParent, true, _ => { if (++progressCalls > 1) throw new OperationCanceledException(); }));
                Assert.That(progressCalls, Is.GreaterThan(1), "Cancellation must occur after source replacement.");
                Assert.That(FileHash(VoxelProductionFamily.SourcePath(manifest.lods[1])), Is.EqualTo(beforeCancel));
                Assert.That(File.ReadAllText(VoxelLodPipeline.AssetPathToAbsolute(manifestPath)), Is.EqualTo(manifestBefore));
                string moved = folder + "/Renamed.prefab";
                Assert.That(AssetDatabase.MoveAsset(manifest.prefabAssetPath, moved), Is.Empty);
                VoxelProductionFamily.RebuildLevel(manifestPath, 0);
                Assert.That(VoxelProductionFamily.Load(manifestPath).prefabAssetPath, Is.EqualTo(moved));
            }
            finally
            {
                if (instance != null) Object.DestroyImmediate(instance);
                AssetDatabase.DeleteAsset(folder);
                if (sharedMaterialPath != null) AssetDatabase.DeleteAsset(sharedMaterialPath);
            }
        }

        [Test]
        public void AutomaticGeneration_Lod0OnlyKeepsAdaptationAndCheckpointOptionsIndependent()
        {
            string folder = "Assets/VoxelLodCountTest_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder));
            var root = new GameObject("Batch");
            var source = GameObject.CreatePrimitive(PrimitiveType.Cube);
            source.transform.SetParent(root.transform, false);
            try
            {
                var profile = ScriptableObject.CreateInstance<VoxelStyleProfile>();
                AssetDatabase.CreateAsset(profile, folder + "/Profile.asset");
                var serialized = new SerializedObject(profile);
                serialized.FindProperty("baseVoxelSize").floatValue = 0.5f;
                serialized.ApplyModifiedPropertiesWithoutUndo();
                var options = new VoxelLodBuildOptions { ExportFolder = folder, GenerateLod0Only = true,
                    ColorMode = VoxelColorMode.SingleColor, SingleColor = Color.white };
                var safety = new VoxelLodBatchOptions { EnableCheckpoint = false, MaximumInitialLodIndex = 2 };
                var plans = VoxelLodPipeline.GetAutomaticBatchPlans(root, safety);
                string onlySignature = VoxelLodBatchAnalyzer.CreateSignature(plans, profile, options, safety);
                var preflight = VoxelLodBatchAnalyzer.Analyze(plans, profile, options, safety);
                Assert.That(preflight.Sources.Single().LodPlans.Length, Is.EqualTo(1));
                var result = VoxelLodPipeline.GenerateAutomatic(source, profile, options, initialVoxelMultiplier: 2);
                Assert.That(result.VoxAssetPaths.Length, Is.EqualTo(1));
                Assert.That(VoxelLodPipeline.TryReadManifest(result.ManifestAssetPath, out var manifest), Is.True);
                Assert.That(manifest.lods.Single().multiplier, Is.EqualTo(2));
                options.GenerateLod0Only = false;
                Assert.That(VoxelLodBatchAnalyzer.CreateSignature(plans, profile, options, safety), Is.Not.EqualTo(onlySignature));
                result = VoxelLodPipeline.GenerateAutomatic(source, profile, options);
                Assert.That(result.VoxAssetPaths.Length, Is.EqualTo(profile.LodCount));
            }
            finally { Object.DestroyImmediate(root); AssetDatabase.DeleteAsset(folder); }
        }

        private static string FileHash(string assetPath)
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            return BitConverter.ToString(sha.ComputeHash(File.ReadAllBytes(VoxelLodPipeline.AssetPathToAbsolute(assetPath))));
        }

        private static void WriteSource(string path, VoxelGrid grid, VoxelColorPalette colors, VoxelSurfacePalette surfaces)
        {
            var quantized = VoxelSemanticQuantizer.Quantize(grid, colors, surfaces);
            var write = VoxelChunkedVoxWriter.Write(VoxelLodPipeline.AssetPathToAbsolute(path), grid, quantized, 16);
            Assert.That(VoxelSemanticTransport.TryCreateMetadata(quantized.SemanticSlots, colors, surfaces, out var semantic, out string error), Is.True, error);
            var metadata = new VoxelBridgeMetadata { formatVersion = 4, sourceName = "Source", voxelSize = grid.VoxelSize,
                baseVoxelSize = grid.VoxelSize, gridOrigin = grid.Origin, unityGridSize = grid.Size, chunks = write.Chunks,
                sourceBoundsMax = (Vector3)grid.Size * grid.VoxelSize, chunkCellSize = 16, semantic = semantic };
            string sidecar = VoxelImporterIntegration.GetMetadataAssetPath(path);
            File.WriteAllText(VoxelLodPipeline.AssetPathToAbsolute(sidecar), JsonUtility.ToJson(metadata, true));
            AssetDatabase.ImportAsset(sidecar, ImportAssetOptions.ForceSynchronousImport);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
        }
    }
}
