using System;
using System.IO;
using System.Linq;
using LocalModels.VoxelBridge.Diagnostics;
using NUnit.Framework;
using Unity.Collections;
using Unity.Entities;
using Unity.Mathematics;
using Unity.Rendering;
using Unity.Transforms;
using UnityEditor.SceneManagement;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace LocalModels.VoxelBridge.Tests
{
    public sealed class VoxelStressTests
    {
        private World world;
        private EntityManager manager;
        private VoxelStressSpawnSystem system;
        private Entity prefab, owner;
        private Material first, second;
        private Mesh mesh;

        [SetUp]
        public void SetUp()
        {
            world = new World("Voxel Stress Test World"); manager = world.EntityManager;
            system = world.GetOrCreateSystemManaged<VoxelStressSpawnSystem>();
            first = new Material(Shader.Find("HDRP/Lit")) { name = "Same Name" };
            second = new Material(Shader.Find("HDRP/Lit")) { name = "Same Name" };
            mesh = new Mesh { name = "Shared Mesh", vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }, triangles = new[] { 0, 1, 2 } };
            prefab = manager.CreateEntity(typeof(Prefab), typeof(LocalTransform));
            manager.SetComponentData(prefab, LocalTransform.Identity);
            manager.AddBuffer<LinkedEntityGroup>(prefab).Add(new LinkedEntityGroup { Value = prefab });
            for (int i = 0; i < 2; i++)
            {
                var child = manager.CreateEntity(typeof(Prefab), typeof(Parent), typeof(LocalTransform), typeof(MaterialMeshInfo), typeof(WorldRenderBounds));
                manager.SetComponentData(child, new Parent { Value = prefab });
                manager.SetComponentData(child, LocalTransform.Identity);
                manager.SetComponentData(child, MaterialMeshInfo.FromRenderMeshArrayIndices(i, 0));
                manager.SetComponentData(child, new WorldRenderBounds { Value = new AABB { Center = new float3(0, 0, i == 0 ? 10 : -10), Extents = new float3(.5f) } });
                manager.AddSharedComponentManaged(child, new RenderMeshArray(new[] { first, second }, new[] { mesh }));
                manager.GetBuffer<LinkedEntityGroup>(prefab).Add(new LinkedEntityGroup { Value = child });
            }
            owner = manager.CreateEntity(typeof(VoxelStressSpawner));
            manager.AddBuffer<VoxelStressSpawnedRoot>(owner);
            manager.SetComponentData(owner, new VoxelStressSpawner { Prefab = prefab, Count = 5, Columns = 3,
                InstancesPerFrame = 2, Spacing = 2, Origin = new float3(10, 0, 2), Rotation = quaternion.RotateY(math.PI * .5f) });
        }

        [TearDown]
        public void TearDown()
        {
            world?.Dispose();
            if (first != null) Object.DestroyImmediate(first);
            if (second != null) Object.DestroyImmediate(second);
            if (mesh != null) Object.DestroyImmediate(mesh);
        }

        private void Mode(VoxelStressMode mode)
        {
            var settings = manager.GetComponentData<VoxelStressSpawner>(owner); settings.Mode = mode; manager.SetComponentData(owner, settings);
        }

        private Entity[] Roots() => manager.GetBuffer<VoxelStressSpawnedRoot>(owner).AsNativeArray().ToArray().Select(e => e.Value).ToArray();

        [Test]
        public void Spawn_IsManualBatchedResumableAndKeepsPrefabReferences()
        {
            system.Update(); Assert.That(Roots(), Is.Empty);
            Mode(VoxelStressMode.Spawning); system.Update(); Assert.That(Roots().Length, Is.EqualTo(2));
            Mode(VoxelStressMode.Idle); system.Update(); Assert.That(Roots().Length, Is.EqualTo(2));
            Mode(VoxelStressMode.Spawning); system.Update(); system.Update();
            Assert.That(Roots().Length, Is.EqualTo(5));
            var settings = manager.GetComponentData<VoxelStressSpawner>(owner);
            Assert.That(settings.Mode, Is.EqualTo(VoxelStressMode.Idle));
            int index = 0;
            foreach (var root in Roots())
            {
                Assert.That(manager.HasComponent<Prefab>(root), Is.False);
                var transform = manager.GetComponentData<LocalTransform>(root);
                Assert.That(math.distance(transform.Position, VoxelStressSpawnSystem.Position(settings, index++)), Is.LessThan(.0001f));
                Assert.That(transform.Scale, Is.EqualTo(1));
                var linked = manager.GetBuffer<LinkedEntityGroup>(root);
                Assert.That(linked.Length, Is.EqualTo(3));
                Assert.That(manager.GetComponentData<Parent>(linked[1].Value).Value, Is.EqualTo(root));
                var array = manager.GetSharedComponentManaged<RenderMeshArray>(linked[1].Value);
                Assert.That(array.GetMaterial(manager.GetComponentData<MaterialMeshInfo>(linked[1].Value)), Is.EqualTo(first));
            }
            system.Update(); Assert.That(Roots().Length, Is.EqualTo(5));
            Assert.That(manager.GetBuffer<LinkedEntityGroup>(prefab).Length, Is.EqualTo(3));
        }

        [Test]
        public void ClearAndOwnerRemoval_DestroyOnlyOwnedLinkedGroups()
        {
            Mode(VoxelStressMode.Spawning); system.Update(); system.Update(); system.Update();
            var spawned = Roots().SelectMany(r => manager.GetBuffer<LinkedEntityGroup>(r).AsNativeArray().ToArray().Select(e => e.Value)).ToArray();
            var unrelated = manager.CreateEntity();
            Mode(VoxelStressMode.Clearing); system.Update();
            Assert.That(Roots().Length, Is.EqualTo(3));
            manager.DestroyEntity(owner);
            Assert.That(manager.Exists(owner), Is.True, "Cleanup buffer must survive owner deletion.");
            system.Update();
            Assert.That(spawned.All(e => !manager.Exists(e)), Is.True);
            Assert.That(manager.Exists(owner), Is.False);
            Assert.That(manager.Exists(unrelated), Is.True);
            Assert.That(manager.Exists(prefab), Is.True);
        }

        [Test]
        public void Snapshot_DistinguishesSameNameMaterialsAndCameraFrustum()
        {
            Mode(VoxelStressMode.Spawning); system.Update();
            var scene = EditorSceneManager.NewPreviewScene();
            var go = new GameObject("Diagnostic Camera", typeof(Camera)); SceneManager.MoveGameObjectToScene(go, scene);
            try
            {
                var camera = go.GetComponent<Camera>(); camera.aspect = 1; camera.nearClipPlane = .1f; camera.farClipPlane = 100;
                var snapshot = VoxelStressSnapshot.Capture(world, owner, camera);
                Assert.That(snapshot.Roots, Is.EqualTo(2)); Assert.That(snapshot.RenderEntities, Is.EqualTo(4));
                Assert.That(snapshot.Materials.Count, Is.EqualTo(2)); Assert.That(snapshot.Meshes.Count, Is.EqualTo(1));
                Assert.That(snapshot.Materials[first], Is.EqualTo(2)); Assert.That(snapshot.Materials[second], Is.EqualTo(2));
                Assert.That(snapshot.Inside, Is.EqualTo(2)); Assert.That(snapshot.Outside, Is.EqualTo(2));
                Assert.That(snapshot.MissingResources, Is.Zero);
                camera.transform.position = new Vector3(100, 0, 0);
                snapshot = VoxelStressSnapshot.Capture(world, owner, camera);
                Assert.That(snapshot.Inside, Is.Zero); Assert.That(snapshot.Outside, Is.EqualTo(4));
                system.Update(); system.Update();
                snapshot = VoxelStressSnapshot.Capture(world, owner, null);
                Assert.That(snapshot.Materials.Count, Is.EqualTo(2)); Assert.That(snapshot.Materials[first], Is.EqualTo(5));
                Assert.That(snapshot.Meshes.Count, Is.EqualTo(1)); Assert.That(snapshot.HasCamera, Is.False);
            }
            finally { Object.DestroyImmediate(go); EditorSceneManager.ClosePreviewScene(scene); }
        }

        [Test]
        public void Validation_RejectsUnsafeCountsAndNonPrefabSources()
        {
            var settings = manager.GetComponentData<VoxelStressSpawner>(owner);
            settings.Count = VoxelStressSpawnSystem.MaximumInstances + 1;
            Assert.Throws<ArgumentException>(() => VoxelStressSpawnSystem.Validate(settings));
            settings.Count = 10; settings.Spacing = float.MaxValue;
            Assert.Throws<ArgumentException>(() => VoxelStressSpawnSystem.Validate(settings));
            settings.Spacing = 2;
            settings.Count = 10; settings.Prefab = owner;
            Assert.Throws<InvalidOperationException>(() => VoxelStressSpawnSystem.ValidatePrefab(manager, settings));
            settings.Prefab = prefab; settings.Count = VoxelStressSpawnSystem.MaximumInstances;
            for (int i = 0; i < 30; i++) manager.GetBuffer<LinkedEntityGroup>(prefab).Add(new LinkedEntityGroup { Value = prefab });
            Assert.Throws<InvalidOperationException>(() => VoxelStressSpawnSystem.ValidatePrefab(manager, settings));
            Assert.That(Roots(), Is.Empty);
        }

        [Test]
        public void Baking_PreservesProductionLodsAndCreatesManualSpawner()
        {
            string folder = "Assets/VoxelStressBakingTest_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder));
            var scene = EditorSceneManager.NewPreviewScene();
            GameObject go = null;
            string sharedMaterial = null;
            try
            {
                var colors = ScriptableObject.CreateInstance<VoxelColorPalette>();
                var surfaces = ScriptableObject.CreateInstance<VoxelSurfacePalette>();
                var profile = ScriptableObject.CreateInstance<VoxelStyleProfile>();
                AssetDatabase.CreateAsset(colors, folder + "/Colors.asset");
                AssetDatabase.CreateAsset(surfaces, folder + "/Surfaces.asset");
                AssetDatabase.CreateAsset(profile, folder + "/Profile.asset");
                Assert.That(VoxelPaletteLutGenerator.TryRebuild(colors, out _, out string error), Is.True, error);
                Assert.That(VoxelPaletteLutGenerator.TryRebuild(surfaces, out _, out error), Is.True, error);
                var grid = new VoxelGrid(Vector3Int.one * 2, Vector3.zero, .125f, true);
                for (int i = 0; i < grid.Occupied.Length; i++) { grid.Occupied[i] = true; grid.SemanticIds[i] = VoxelSemanticEncoding.Pack(54, 13); }
                var quantized = VoxelSemanticQuantizer.Quantize(grid, colors, surfaces);
                string source = folder + "/Source.vox";
                var write = VoxelChunkedVoxWriter.Write(VoxelLodPipeline.AssetPathToAbsolute(source), grid, quantized, 128);
                Assert.That(VoxelSemanticTransport.TryCreateMetadata(quantized.SemanticSlots, colors, surfaces, out var semantic, out error), Is.True, error);
                var metadata = new VoxelBridgeMetadata { formatVersion = 4, sourceName = "Source", voxelSize = .125f, baseVoxelSize = .125f,
                    unityGridSize = grid.Size, chunks = write.Chunks, semantic = semantic, sourceBoundsMax = Vector3.one * .25f };
                string sidecar = VoxelImporterIntegration.GetMetadataAssetPath(source);
                File.WriteAllText(VoxelLodPipeline.AssetPathToAbsolute(sidecar), JsonUtility.ToJson(metadata, true));
                AssetDatabase.ImportAsset(sidecar, ImportAssetOptions.ForceSynchronousImport);
                AssetDatabase.ImportAsset(source, ImportAssetOptions.ForceSynchronousImport);
                string family = VoxelProductionFamily.Create(source, profile, folder + "/Output");
                VoxelProductionFamily.DeriveLevel(family, 1, VoxelLodGenerationMode.ReduceParent);
                var production = AssetDatabase.LoadAssetAtPath<GameObject>(VoxelProductionFamily.Load(family).prefabAssetPath);
                sharedMaterial = AssetDatabase.GetAssetPath(production.GetComponentInChildren<MeshRenderer>().sharedMaterial);
                go = new GameObject("Baked Stress Spawner", typeof(VoxelStressSpawnerAuthoring));
                SceneManager.MoveGameObjectToScene(go, scene);
                var data = new SerializedObject(go.GetComponent<VoxelStressSpawnerAuthoring>());
                data.FindProperty("prefab").objectReferenceValue = production;
                data.FindProperty("count").intValue = 3;
                data.FindProperty("instancesPerFrame").intValue = 2;
                data.ApplyModifiedPropertiesWithoutUndo();
                using var blobs = new BlobAssetStore(128);
                using var baked = new World("Voxel Stress Baking World");
                // Entities 1.4 exposes its isolated baking entry point internally; reflection stays in this test.
                var hybrid = typeof(Baker<>).Assembly;
                var settingsType = hybrid.GetType("Unity.Entities.BakingSettings", true);
                var settings = Activator.CreateInstance(settingsType);
                settingsType.GetProperty("BlobAssetStore").SetValue(settings, blobs);
                // Render mesh postprocessing requires a SceneSection, supplied by a nonzero scene GUID.
                settingsType.GetField("SceneGUID").SetValue(settings, new Unity.Entities.Hash128(1, 2, 3, 4));
                hybrid.GetType("Unity.Entities.BakingUtility", true).GetMethod("BakeGameObjects",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)
                    .Invoke(null, new[] { (object)baked, new[] { go }, settings });
                var em = baked.EntityManager;
                using var query = em.CreateEntityQuery(typeof(VoxelStressSpawner), typeof(VoxelStressSpawnedRoot));
                Assert.That(query.CalculateEntityCount(), Is.EqualTo(1));
                Entity spawner = query.GetSingletonEntity();
                var configuration = em.GetComponentData<VoxelStressSpawner>(spawner);
                Assert.That(configuration.Mode, Is.EqualTo(VoxelStressMode.Idle));
                VoxelStressSpawnSystem.ValidatePrefab(em, configuration);
                var runtime = baked.GetOrCreateSystemManaged<VoxelStressSpawnSystem>();
                configuration.Mode = VoxelStressMode.Spawning; em.SetComponentData(spawner, configuration);
                runtime.Update(); runtime.Update();
                Assert.That(em.GetBuffer<VoxelStressSpawnedRoot>(spawner).Length, Is.EqualTo(3));
                var snapshot = VoxelStressSnapshot.Capture(baked, spawner, null);
                Assert.That(snapshot.RenderEntities, Is.EqualTo(6));
                Assert.That(snapshot.Materials.Count, Is.EqualTo(1));
                Assert.That(snapshot.Materials.Keys.Single().shader.name, Is.EqualTo("Voxel Bridge/VoxelWorldOpaque"));
                Assert.That(snapshot.Meshes.Count, Is.EqualTo(2));
                var linked = em.GetBuffer<LinkedEntityGroup>(em.GetBuffer<VoxelStressSpawnedRoot>(spawner)[0].Value);
                Assert.That(linked.AsNativeArray().ToArray().Any(e => em.HasComponent<MeshLODGroupComponent>(e.Value)), Is.True);
                Assert.That(linked.AsNativeArray().ToArray().Count(e => em.HasComponent<MeshLODComponent>(e.Value)), Is.EqualTo(2));
            }
            finally
            {
                if (go != null) Object.DestroyImmediate(go);
                EditorSceneManager.ClosePreviewScene(scene);
                AssetDatabase.DeleteAsset(folder);
                if (sharedMaterial != null) AssetDatabase.DeleteAsset(sharedMaterial);
            }
        }
    }
}
