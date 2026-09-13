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
    public sealed class VoxelGlassTests
    {
        private VoxelSurfacePalette surfaces;
        private static readonly ushort Opaque = VoxelSemanticEncoding.Pack(5, 0);
        private static readonly ushort Glass = VoxelSemanticEncoding.Pack(54, 44);

        [SetUp] public void SetUp() => surfaces = ScriptableObject.CreateInstance<VoxelSurfacePalette>();
        [TearDown] public void TearDown() { if (surfaces != null && !AssetDatabase.Contains(surfaces)) Object.DestroyImmediate(surfaces); }

        private static VoxelGrid Solid(Vector3Int size, ushort pair)
        {
            var grid = new VoxelGrid(size, Vector3.zero, 1, true);
            for (int i = 0; i < grid.Occupied.Length; i++) { grid.Occupied[i] = true; grid.SemanticIds[i] = pair; }
            return grid;
        }

        [Test]
        public void OpaqueOnlyMesh_RemainsByteEquivalentInItsVertexAndIndexStreams()
        {
            var grid = Solid(new Vector3Int(3, 2, 1), Opaque);
            var original = VoxelSemanticMesher.Build(grid, true);
            var current = VoxelSemanticMesher.Build(grid, true, palette: surfaces);
            try
            {
                Assert.That(current.subMeshCount, Is.EqualTo(1));
                CollectionAssert.AreEqual(original.vertices, current.vertices);
                CollectionAssert.AreEqual(original.normals, current.normals);
                CollectionAssert.AreEqual(original.uv, current.uv);
                CollectionAssert.AreEqual(original.uv4, current.uv4);
                CollectionAssert.AreEqual(original.triangles, current.triangles);
            }
            finally { Object.DestroyImmediate(original); Object.DestroyImmediate(current); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void AdjacentOpaqueAndGlass_KeepOpaqueInterfaceWithoutDuplicateGlassFace(bool hide)
        {
            var grid = Solid(new Vector3Int(2, 1, 1), Opaque); grid.SemanticIds[1] = Glass;
            var mesh = VoxelSemanticMesher.Build(grid, hide, palette: surfaces);
            try
            {
                Assert.That(mesh.subMeshCount, Is.EqualTo(2));
                Assert.That(mesh.GetIndexCount(0), Is.EqualTo(36));
                Assert.That(mesh.GetIndexCount(1), Is.EqualTo(30));
                foreach (int i in mesh.GetTriangles(0)) Assert.That(mesh.uv4[i].y, Is.Zero);
                foreach (int i in mesh.GetTriangles(1)) Assert.That(mesh.uv4[i], Is.EqualTo(new Vector2(44, 1)));
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void AdjacentGlass_MergesWithoutInternalFaces()
        {
            var grid = Solid(new Vector3Int(2, 1, 1), Glass);
            var mesh = VoxelSemanticMesher.Build(grid, true, palette: surfaces);
            try
            {
                Assert.That(mesh.GetIndexCount(0), Is.Zero);
                Assert.That(mesh.GetIndexCount(1), Is.EqualTo(36));
                Assert.That(mesh.vertexCount, Is.EqualTo(24));
            }
            finally { Object.DestroyImmediate(mesh); }
        }

        [Test]
        public void GlassShell_DoesNotHideOpaqueGeometryBehindIt()
        {
            var grid = Solid(Vector3Int.one * 3, Glass); grid.SemanticIds[grid.Index(1, 1, 1)] = Opaque;
            var mesh = VoxelSemanticMesher.Build(grid, true, palette: surfaces);
            try { Assert.That(mesh.GetIndexCount(0), Is.EqualTo(36)); }
            finally { Object.DestroyImmediate(mesh); }
        }

        [TestCase(0f, 1)]
        [TestCase(.25f, 1)]
        [TestCase(1f, 1)]
        [TestCase(.25f, 0)]
        public void FillInterior_GlassWindowPreservesCavityAndSampledCells(float opacity, int padding)
        {
            // Use a non-default ID so the traversal must consult the palette's render class.
            const int glassId = 200;
            surfaces.MutableEntries.Add(new VoxelSurfaceDefinition(glassId, "Window", VoxelSurfaceRenderClass.Transparent, 0, .9f, 0, 1, opacity));
            var grid = HollowBox(padding, semantic: true);
            int window = grid.Index(padding + 2, padding + 2, padding);
            grid.SemanticIds[window] = VoxelSemanticEncoding.Pack(54, glassId);
            var occupied = (bool[])grid.Occupied.Clone();
            var pairs = (ushort[])grid.SemanticIds.Clone();
            MeshVoxelizer.FillInterior(grid, surfaces);
            CollectionAssert.AreEqual(occupied, grid.Occupied, "No air may be filled behind a glass window, including at the grid boundary.");
            CollectionAssert.AreEqual(pairs, grid.SemanticIds);
        }

        [TestCase(false)]
        [TestCase(true)]
        public void FillInterior_OpaqueShellStillFillsInRgbAndSemanticModes(bool semantic)
        {
            var grid = HollowBox(1, semantic);
            // A palette must not turn RGB data into semantic data or change legacy filling.
            MeshVoxelizer.FillInterior(grid, surfaces);
            Assert.That(grid.CountOccupied(), Is.EqualTo(125));
            for (int z = 0; z < grid.Size.z; z++)
            for (int y = 0; y < grid.Size.y; y++)
            for (int x = 0; x < grid.Size.x; x++)
            {
                int i = grid.Index(x, y, z);
                bool inside = x >= 1 && x <= 5 && y >= 1 && y <= 5 && z >= 1 && z <= 5;
                Assert.That(grid.Occupied[i], Is.EqualTo(inside));
                if (!inside) continue;
                if (semantic) Assert.That(grid.SemanticIds[i], Is.EqualTo(Opaque));
                else Assert.That(grid.Colors[i], Is.EqualTo(new Color32(31, 63, 127, 255)));
            }
        }

        [Test]
        public void FillInterior_TraversesThickGlassWithoutCreatingGlassVolume()
        {
            var grid = new VoxelGrid(Vector3Int.one * 9, Vector3.zero, .03125f, true);
            for (int z = 1; z <= 7; z++)
            for (int y = 1; y <= 7; y++)
            for (int x = 1; x <= 7; x++)
            {
                if (x >= 3 && x <= 5 && y >= 3 && y <= 5 && z >= 3 && z <= 5) continue;
                int i = grid.Index(x, y, z); grid.Occupied[i] = true; grid.SemanticIds[i] = Glass;
            }
            var occupied = (bool[])grid.Occupied.Clone();
            var pairs = (ushort[])grid.SemanticIds.Clone();
            MeshVoxelizer.FillInterior(grid, surfaces);
            CollectionAssert.AreEqual(occupied, grid.Occupied);
            CollectionAssert.AreEqual(pairs, grid.SemanticIds);
        }

        private static VoxelGrid HollowBox(int padding, bool semantic)
        {
            var grid = new VoxelGrid(Vector3Int.one * (5 + padding * 2), new Vector3(-2, 3, 5), .03125f, semantic);
            for (int z = 0; z < 5; z++)
            for (int y = 0; y < 5; y++)
            for (int x = 0; x < 5; x++)
            {
                if (x > 0 && x < 4 && y > 0 && y < 4 && z > 0 && z < 4) continue;
                int i = grid.Index(x + padding, y + padding, z + padding);
                grid.Occupied[i] = true;
                if (semantic) grid.SemanticIds[i] = Opaque;
                else grid.Colors[i] = new Color32(31, 63, 127, 255);
            }
            return grid;
        }

        [Test]
        public void ChunkBoundary_KeepsTheSameVisibleInterfaces()
        {
            var grid = new VoxelGrid(new Vector3Int(20, 1, 1), new Vector3(-3, 2, 1), 1, true);
            grid.Occupied[15] = grid.Occupied[16] = true; grid.SemanticIds[15] = Glass; grid.SemanticIds[16] = Opaque;
            var chunks = VoxelSemanticMesher.BuildChunks(grid, 16, true, palette: surfaces);
            try
            {
                Assert.That(chunks.Sum(m => (int)m.GetIndexCount(0)), Is.EqualTo(36));
                Assert.That(chunks.Where(m => m.subMeshCount > 1).Sum(m => (int)m.GetIndexCount(1)), Is.EqualTo(30));
            }
            finally { foreach (var m in chunks) Object.DestroyImmediate(m); }
        }

        [Test]
        public void Lut_UsesOpacityOnlyForGlassAndRejectsInvalidOpacity()
        {
            surfaces.TryGetSurface(44, out var glass);
            Assert.That(VoxelPaletteLutBuilder.EncodeSurface(glass).a, Is.EqualTo(64));
            surfaces.TryGetSurface(1, out var concrete);
            Assert.That(VoxelPaletteLutBuilder.EncodeSurface(concrete).a, Is.EqualTo(230));
            surfaces.MutableEntries[44] = new VoxelSurfaceDefinition(44, "Glass", VoxelSurfaceRenderClass.Transparent, 0, .9f, 0, 1, float.NaN);
            Assert.That(surfaces.TryValidate(out _), Is.False);
        }

        [Test]
        public void AppearancePreview_ChangesTopologyAcrossClassesWithoutChangingTheDraft()
        {
            var grid = Solid(new Vector3Int(2, 1, 1), Opaque);
            var before = grid.SemanticIds.ToArray();
            using var preview = new VoxelPainterAppearancePreview(grid, new HashSet<int> { 1 }, true, palette: surfaces);
            Mesh opaque = preview.Mesh;
            preview.SetColor(63); preview.SetSurface(44);
            Assert.That(opaque == null, Is.True);
            Assert.That(preview.Mesh.subMeshCount, Is.EqualTo(2));
            Assert.That(preview.Mesh.GetTriangles(1).All(i => preview.Mesh.uv[i].x == 63), Is.True);
            Mesh glass = preview.Mesh; preview.SetSurface(44);
            Assert.That(preview.Mesh, Is.SameAs(glass));
            preview.SetSurface(7);
            Assert.That(glass == null, Is.True);
            Assert.That(preview.Mesh.subMeshCount, Is.EqualTo(1));
            CollectionAssert.AreEqual(before, grid.SemanticIds);
        }

        [Test]
        public void Shader_IsTransparentAndHasNoCompilationErrors()
        {
            var shader = VoxelProductionExporter.GlassShader();
            var material = new Material(shader);
            try
            {
                Assert.That(material.GetFloat("_SurfaceType"), Is.EqualTo(1));
                Assert.That(material.GetFloat("_AlphaCutoffEnable"), Is.EqualTo(1), "Render-class rejection must use HDRP's native alpha test.");
                Assert.That(material.renderQueue, Is.EqualTo(3000));
                Assert.That(ShaderUtil.ShaderHasError(shader), Is.False);
            }
            finally { Object.DestroyImmediate(material); }
        }

        [TestCase("Forward", false)]
        [TestCase("Forward", true)]
        [TestCase("ForwardDXR", false)]
        [TestCase("IndirectDXR", false)]
        [TestCase("VisibilityDXR", false)]
        [TestCase("GBufferDXR", false)]
        [TestCase("DebugDXR", false)]
        [TestCase("PathTracingDXR", false)]
        public void Shader_CompilesRasterAndRayTracingVariants(string passName, bool dots)
        {
            if (Application.platform != RuntimePlatform.WindowsEditor)
                Assert.Ignore("This regression compiles the Windows D3D variants used by HDRP.");

            // Import alone does not compile DXR hit programs. A clip() in a custom function
            // can pass raster checks and only fail when HDRP requests ForwardDXR later.
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(VoxelProductionExporter.GlassShaderPath);
            Assert.That(shader, Is.Not.Null);
            var keywords = new List<string> { "PROBE_VOLUMES_L2", "_DISABLE_DECALS", "_DISABLE_SSR_TRANSPARENT", "_SURFACE_TYPE_TRANSPARENT", "_ALPHATEST_ON" };
            if (passName == "Forward")
                keywords.AddRange(new[] { "PUNCTUAL_SHADOW_MEDIUM", "DIRECTIONAL_SHADOW_MEDIUM", "AREA_SHADOW_MEDIUM" });
            if (dots) keywords.Add("DOTS_INSTANCING_ON");
            var data = ShaderUtil.GetShaderData(shader);
            for (int i = 0; i < data.SubshaderCount; i++)
            {
                var subshader = data.GetSubshader(i);
                for (int j = 0; j < subshader.PassCount; j++)
                {
                    var pass = subshader.GetPass(j);
                    if (pass.Name != passName) continue;
                    var result = pass.CompileVariant(
                        passName.EndsWith("DXR", StringComparison.Ordinal)
                            ? UnityEditor.Rendering.ShaderType.RayTracing : UnityEditor.Rendering.ShaderType.Fragment,
                        keywords.ToArray(), UnityEditor.Rendering.ShaderCompilerPlatform.D3D, BuildTarget.StandaloneWindows64);
                    Assert.That(result.Success, Is.True, passName + ": " + string.Join("\n", result.Messages.Select(m => m.message)));
                    return;
                }
            }
            Assert.Fail("Missing HDRP pass: " + passName);
        }

        [Test]
        public void SurfacePreview_SwitchesBackFromGlassToOpaque()
        {
            var material = new Material(Shader.Find("HDRP/Lit")) { hideFlags = HideFlags.HideAndDontSave };
            try
            {
                surfaces.TryGetSurface(44, out var glass); surfaces.TryGetSurface(0, out var opaque);
                VoxelSurfacePreviewWindow.ApplySurface(material, glass, Color.cyan);
                Assert.That(material.GetColor("_BaseColor").a, Is.EqualTo(.25f));
                Assert.That(material.renderQueue, Is.GreaterThanOrEqualTo(3000));
                VoxelSurfacePreviewWindow.ApplySurface(material, opaque, Color.white);
                Assert.That(material.GetColor("_BaseColor").a, Is.EqualTo(1));
                Assert.That(material.renderQueue, Is.LessThan(2500));
            }
            finally { Object.DestroyImmediate(material); }
        }

        [Test]
        public void FamilyAndStandalone_KeepGlassMaterialsAcrossRebuildReductionAndSurfaceEdits()
        {
            string folder = "Assets/VoxelGlassTest_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder));
            var createdMaterials = new HashSet<string>();
            try
            {
                var colors = ScriptableObject.CreateInstance<VoxelColorPalette>();
                var profile = ScriptableObject.CreateInstance<VoxelStyleProfile>();
                AssetDatabase.CreateAsset(colors, folder + "/Colors.asset");
                AssetDatabase.CreateAsset(surfaces, folder + "/Surfaces.asset");
                AssetDatabase.CreateAsset(profile, folder + "/Profile.asset");
                Assert.That(VoxelPaletteLutGenerator.TryRebuild(colors, out _, out string error), Is.True, error);
                Assert.That(VoxelPaletteLutGenerator.TryRebuild(surfaces, out _, out error), Is.True, error);
                var grid = Solid(new Vector3Int(8, 4, 4), Glass);
                for (int z = 0; z < 4; z++) for (int y = 0; y < 4; y++) for (int x = 0; x < 4; x++) grid.SemanticIds[grid.Index(x, y, z)] = Opaque;
                string source = folder + "/Source.vox";
                var quantized = VoxelSemanticQuantizer.Quantize(grid, colors, surfaces);
                var write = VoxelChunkedVoxWriter.Write(VoxelLodPipeline.AssetPathToAbsolute(source), grid, quantized, 128);
                Assert.That(VoxelSemanticTransport.TryCreateMetadata(quantized.SemanticSlots, colors, surfaces, out var semantic, out error), Is.True, error);
                var metadata = new VoxelBridgeMetadata { formatVersion = 4, sourceName = "Glass Fixture", voxelSize = 1, baseVoxelSize = 1,
                    gridOrigin = grid.Origin, unityGridSize = grid.Size, chunks = write.Chunks, sourceBoundsMax = (Vector3)grid.Size,
                    chunkCellSize = 128, semantic = semantic, hideInternalCavities = true };
                string sidecar = VoxelImporterIntegration.GetMetadataAssetPath(source);
                File.WriteAllText(sidecar, JsonUtility.ToJson(metadata, true));
                AssetDatabase.ImportAsset(sidecar, ImportAssetOptions.ForceSynchronousImport);
                AssetDatabase.ImportAsset(source, ImportAssetOptions.ForceSynchronousImport);
                var standalone = VoxelProductionExporter.Export(source, folder + "/Standalone");
                foreach (var m in standalone.GetComponent<MeshRenderer>().sharedMaterials) createdMaterials.Add(AssetDatabase.GetAssetPath(m));
                Assert.That(standalone.GetComponent<MeshRenderer>().sharedMaterials.Length, Is.EqualTo(2));
                var edit = new VoxelSurfaceEdit(source);
                edit.Apply(Enumerable.Range(0, grid.Occupied.Length).ToArray(), 0);
                VoxelSurfaceEditStore.Save(source, edit);
                VoxelProductionExporter.Rebuild(AssetDatabase.GetAssetPath(standalone));
                Assert.That(standalone.GetComponent<MeshRenderer>().sharedMaterials.Length, Is.EqualTo(1));
                Undo.PerformUndo();
                Assert.That(standalone.GetComponent<MeshFilter>().sharedMesh.subMeshCount, Is.EqualTo(2));
                Assert.That(standalone.GetComponent<MeshRenderer>().sharedMaterials.Length, Is.EqualTo(2));
                Undo.PerformRedo();
                Assert.That(standalone.GetComponent<MeshFilter>().sharedMesh.subMeshCount, Is.EqualTo(1));
                Assert.That(standalone.GetComponent<MeshRenderer>().sharedMaterials.Length, Is.EqualTo(1));
                edit = new VoxelSurfaceEdit(source); edit.Apply(Enumerable.Range(0, grid.Occupied.Length).ToArray(), 44);
                VoxelSurfaceEditStore.Save(source, edit);
                VoxelProductionExporter.Rebuild(AssetDatabase.GetAssetPath(standalone));
                Assert.That(standalone.GetComponent<MeshRenderer>().sharedMaterials.Length, Is.EqualTo(2));
                var manifestPath = VoxelProductionFamily.Create(source, profile, folder + "/Family");
                VoxelProductionFamily.DeriveLevel(manifestPath, 1, VoxelLodGenerationMode.ReduceParent);
                VoxelProductionFamily.RebuildAll(manifestPath);
                var family = VoxelProductionFamily.Load(manifestPath);
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(family.prefabAssetPath);
                var expected = standalone.GetComponent<MeshRenderer>().sharedMaterials[1];
                foreach (var renderer in prefab.GetComponentsInChildren<MeshRenderer>())
                    Assert.That(renderer.sharedMaterials[1], Is.SameAs(expected));
                Assert.That(expected.GetShaderPassEnabled("ShadowCaster"), Is.False);
                Assert.That(expected.IsKeywordEnabled("_SURFACE_TYPE_TRANSPARENT"), Is.True);
            }
            finally
            {
                AssetDatabase.DeleteAsset(folder);
                foreach (string material in createdMaterials) AssetDatabase.DeleteAsset(material);
            }
        }

        [Test]
        public void Baking_SeparatesOpaqueAndGlassSubmeshBindings()
        {
            var grid = Solid(new Vector3Int(2, 1, 1), Opaque); grid.SemanticIds[1] = Glass;
            var mesh = VoxelSemanticMesher.Build(grid, true, palette: surfaces);
            var opaque = new Material(AssetDatabase.LoadAssetAtPath<Shader>(VoxelProductionExporter.ShaderPath));
            var glass = new Material(VoxelProductionExporter.GlassShader());
            var scene = UnityEditor.SceneManagement.EditorSceneManager.NewPreviewScene();
            var root = new GameObject("Glass baking fixture");
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, scene);
            root.AddComponent<MeshFilter>().sharedMesh = mesh;
            root.AddComponent<MeshRenderer>().sharedMaterials = new[] { opaque, glass };
            var previous = Unity.Entities.World.DefaultGameObjectInjectionWorld;
            using var world = new Unity.Entities.World("Glass isolated baking test");
            using var store = new Unity.Entities.BlobAssetStore(128);
            try
            {
                Unity.Entities.World.DefaultGameObjectInjectionWorld = world;
                // The installed Entities package exposes baking only internally. Use its real pipeline,
                // including a scene identity so additional render entities receive SceneSection.
                var flags = System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Instance;
                var hybrid = typeof(Unity.Entities.BakingSystem).Assembly;
                var settingsType = hybrid.GetType("Unity.Entities.BakingSettings");
                var settings = Activator.CreateInstance(settingsType, true);
                settingsType.GetField("SceneGUID", flags).SetValue(settings, new Unity.Entities.Hash128(Guid.NewGuid().ToString("N")));
                settingsType.GetProperty("BlobAssetStore", flags).SetValue(settings, store);
                var flagField = settingsType.GetField("BakingFlags", flags);
                flagField.SetValue(settings, Enum.Parse(flagField.FieldType, "AssignName, AddEntityGUID"));
                hybrid.GetType("Unity.Entities.BakingUtility").GetMethod("BakeGameObjects", flags)
                    .Invoke(null, new object[] { world, new[] { root }, settings });
                world.EntityManager.CompleteAllTrackedJobs();
                using var query = world.EntityManager.CreateEntityQuery(typeof(Unity.Rendering.MaterialMeshInfo), typeof(Unity.Rendering.RenderMeshArray));
                using var entities = query.ToEntityArray(Unity.Collections.Allocator.Temp);
                var bindings = new HashSet<string>();
                foreach (var entity in entities)
                {
                    var info = world.EntityManager.GetComponentData<Unity.Rendering.MaterialMeshInfo>(entity);
                    var array = world.EntityManager.GetSharedComponentManaged<Unity.Rendering.RenderMeshArray>(entity);
                    foreach (var material in array.GetMaterials(info)) bindings.Add(info.SubMesh + ":" + material.shader.name);
                }
                CollectionAssert.AreEquivalent(new[] { "0:Voxel Bridge/VoxelWorldOpaque", "1:Voxel Bridge/VoxelGlass" }, bindings);
            }
            finally
            {
                Unity.Entities.World.DefaultGameObjectInjectionWorld = previous;
                Object.DestroyImmediate(root);
                UnityEditor.SceneManagement.EditorSceneManager.ClosePreviewScene(scene);
                foreach (var obj in new Object[] { mesh, opaque, glass }) Object.DestroyImmediate(obj);
            }
        }
    }
}
