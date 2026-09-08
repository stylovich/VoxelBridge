using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace LocalModels.VoxelBridge.Tests
{
    public sealed class VoxelSurfaceEditTests
    {
        private string folder;
        private string path;
        private string sidecarPath;
        private VoxelColorPalette colors;
        private VoxelSurfacePalette surfaces;

        [SetUp]
        public void SetUp()
        {
            string name = "SurfaceEditTest_" + Guid.NewGuid().ToString("N");
            folder = "Assets/" + name;
            AssetDatabase.CreateFolder("Assets", name);
            path = folder + "/Source.vox";
            sidecarPath = VoxelImporterIntegration.GetMetadataAssetPath(path);
            colors = ScriptableObject.CreateInstance<VoxelColorPalette>();
            colors.MutableEntries.Clear();
            for (int i = 0; i < 256; i++) colors.MutableEntries.Add(new VoxelColorDefinition(i, "Color " + i,
                new Color32((byte)i, 96, 128, 255)));
            surfaces = ScriptableObject.CreateInstance<VoxelSurfacePalette>();
            AssetDatabase.CreateAsset(colors, folder + "/Colors.asset");
            AssetDatabase.CreateAsset(surfaces, folder + "/Surfaces.asset");
        }

        [TearDown]
        public void TearDown() { if (folder != null) AssetDatabase.DeleteAsset(folder); }

        [Test]
        public void Save_CommitsPairAndPreservesAssetGuids()
        {
            Create();
            AssetDatabase.ImportAsset(sidecarPath, ImportAssetOptions.ForceSynchronousImport);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            string guid = AssetDatabase.AssetPathToGUID(path), metaGuid = AssetDatabase.AssetPathToGUID(sidecarPath);
            var edit = new VoxelSurfaceEdit(path); edit.Apply(new[] { 0, 34 }, 7);
            VoxelSurfaceEditStore.Save(path, edit);
            Assert.That(VoxelSurfaceEditStore.HasPending(path), Is.False);
            Assert.That(new VoxelSurfaceEdit(path).Grid.SemanticIds[34], Is.EqualTo(VoxelSemanticEncoding.Pack(1, 7)));
            Assert.That(AssetDatabase.AssetPathToGUID(path), Is.EqualTo(guid));
            Assert.That(AssetDatabase.AssetPathToGUID(sidecarPath), Is.EqualTo(metaGuid));
        }

        [Test]
        public void SaveFailure_RestoresBothFilesAndKeepsPendingBuffer()
        {
            Create();
            AssetDatabase.ImportAsset(sidecarPath, ImportAssetOptions.ForceSynchronousImport);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            byte[] vox = File.ReadAllBytes(path), sidecar = File.ReadAllBytes(sidecarPath);
            var edit = new VoxelSurfaceEdit(path); edit.Apply(new[] { 0 }, 7);
            Assert.Throws<IOException>(() => VoxelSurfaceEditStore.Save(path, edit, () => throw new IOException("Injected interruption")));
            CollectionAssert.AreEqual(vox, File.ReadAllBytes(path));
            CollectionAssert.AreEqual(sidecar, File.ReadAllBytes(sidecarPath));
            Assert.That(VoxelSurfaceEditStore.HasPending(path), Is.False);
            Assert.That(edit.PendingCells, Is.EqualTo(1));
            edit.BuildOutput(out _, out _);
        }

        [Test]
        public void PendingRecovery_BlocksSemanticReadsAndRestoresWithoutOverwritingExternalChanges()
        {
            Create();
            AssetDatabase.ImportAsset(sidecarPath, ImportAssetOptions.ForceSynchronousImport);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            string recovery = Path.Combine("Library", "VoxelBridgeSurfaceEdits", AssetDatabase.AssetPathToGUID(path));
            byte[] journal = null, originalVox = null, originalMetadata = null, partialVox = null;
            var edit = new VoxelSurfaceEdit(path); edit.Apply(new[] { 0 }, 7);
            Assert.Throws<IOException>(() => VoxelSurfaceEditStore.Save(path, edit, () =>
            {
                journal = File.ReadAllBytes(Path.Combine(recovery, "transaction.json"));
                originalVox = File.ReadAllBytes(Path.Combine(recovery, "original.vox"));
                originalMetadata = File.ReadAllBytes(Path.Combine(recovery, "original.json"));
                partialVox = File.ReadAllBytes(path);
                throw new IOException("Injected interruption");
            }));
            try
            {
                File.WriteAllBytes(Path.Combine(recovery, "original.vox"), originalVox);
                File.WriteAllBytes(Path.Combine(recovery, "original.json"), originalMetadata);
                File.WriteAllBytes(Path.Combine(recovery, "transaction.json"), journal);
                File.WriteAllBytes(path, partialVox);
                Assert.That(VoxelImporterIntegration.TryLoadMetadata(path, out _, out string error), Is.False);
                StringAssert.Contains("interrupted", error);
                byte[] external = (byte[])partialVox.Clone(); external[4] ^= 1;
                File.WriteAllBytes(path, external);
                Assert.Throws<InvalidOperationException>(() => VoxelSurfaceEditStore.Recover(path));
                CollectionAssert.AreEqual(external, File.ReadAllBytes(path));
                File.WriteAllBytes(path, partialVox);
                VoxelSurfaceEditStore.Recover(path);
                CollectionAssert.AreEqual(originalVox, File.ReadAllBytes(path));
                CollectionAssert.AreEqual(originalMetadata, File.ReadAllBytes(sidecarPath));
            }
            finally
            {
                foreach (string file in new[] { "transaction.json", "original.vox", "original.json" }) File.Delete(Path.Combine(recovery, file));
            }
        }

        [TestCase(0)]
        [TestCase(1)]
        [TestCase(2)]
        public void Picker_FindsNearestOccupiedCellAlongEachAxis(int axis)
        {
            var grid = new VoxelGrid(new Vector3Int(3, 3, 3), new Vector3(-2, 3, 5), .032f, true);
            grid.Occupied[grid.Index(1, 1, 1)] = true;
            Vector3 inside = grid.Origin + Vector3.one * (.032f * 1.5f), direction = Vector3.zero;
            direction[axis] = 1;
            Assert.That(VoxelSurfaceEdit.Pick(grid, new Ray(inside - direction * 2, direction), out int index), Is.True);
            Assert.That(index, Is.EqualTo(grid.Index(1, 1, 1)));
            Assert.That(VoxelSurfaceEdit.Pick(grid, new Ray(inside + direction * 2, -direction), out index), Is.True);
            Assert.That(index, Is.EqualTo(grid.Index(1, 1, 1)));
        }

        [Test]
        public void Picker_DoesNotSelectThroughForegroundOrAlongOuterBoundary()
        {
            var grid = new VoxelGrid(new Vector3Int(3, 3, 3), Vector3.zero, 1, true);
            grid.Occupied[grid.Index(0, 1, 1)] = grid.Occupied[grid.Index(2, 1, 1)] = true;
            Assert.That(VoxelSurfaceEdit.Pick(grid, new Ray(new Vector3(-1, 1.5f, 1.5f), Vector3.right), out int index), Is.True);
            Assert.That(index, Is.EqualTo(grid.Index(0, 1, 1)));
            Assert.That(VoxelSurfaceEdit.Pick(grid, new Ray(new Vector3(-1, 3, 1.5f), Vector3.right), out _), Is.False);
            Assert.That(VoxelSurfaceEdit.Pick(grid, new Ray(Vector3.one, Vector3.zero), out _), Is.False);
            Assert.That(VoxelSurfaceEdit.Pick(grid, new Ray(new Vector3(float.NaN, 0, 0), Vector3.right), out _), Is.False);
        }

        [Test]
        public void Window_RestoresSerializedDraftAndUsesTemporaryPaletteMaterial()
        {
            Create(8);
            Assert.That(VoxelPaletteLutGenerator.TryRebuild(colors, out _, out string error), Is.True, error);
            Assert.That(VoxelPaletteLutGenerator.TryRebuild(surfaces, out _, out error), Is.True, error);
            AssetDatabase.ImportAsset(sidecarPath, ImportAssetOptions.ForceSynchronousImport);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var window = ScriptableObject.CreateInstance<VoxelSurfacePainterWindow>();
            VoxelSurfacePainterWindow restored = null;
            try
            {
                var data = new SerializedObject(window);
                data.FindProperty("sourceGuid").stringValue = AssetDatabase.AssetPathToGUID(path);
                data.FindProperty("source").objectReferenceValue = AssetDatabase.LoadMainAssetAtPath(path);
                data.ApplyModifiedPropertiesWithoutUndo();
                InvokeWindow(window, "LoadSource", false);
                var edit = (VoxelSurfaceEdit)WindowField(window, "edit");
                Assert.That(edit, Is.Not.Null, (string)WindowField(window, "status"));
                InvokeWindow(window, "Change", (Action)(() => edit.Apply(new[] { 0, 1, 2 }, 13)));
                Assert.That(window.hasUnsavedChanges, Is.True);
                string draft = EditorJsonUtility.ToJson(window);
                restored = ScriptableObject.CreateInstance<VoxelSurfacePainterWindow>();
                EditorJsonUtility.FromJsonOverwrite(draft, restored);
                InvokeWindow(restored, "LoadSource", true);
                var restoredEdit = (VoxelSurfaceEdit)WindowField(restored, "edit");
                Assert.That(restoredEdit, Is.Not.Null, (string)WindowField(restored, "status"));
                Assert.That(restoredEdit.PendingCells, Is.EqualTo(3));
                Assert.That(restoredEdit.CanUndo, Is.False, "Domain reload retains the draft, not its transient history.");
                var previewMaterial = (Material)WindowField(restored, "material");
                Assert.That(AssetDatabase.Contains(previewMaterial), Is.False);
                Assert.That(previewMaterial.GetTexture("_PaletteColor") == colors.GeneratedLut, Is.True, "The native color LUT asset must be shared even if Unity recreates its managed wrapper.");
                Assert.That(previewMaterial.GetTexture("_PaletteSurface") == surfaces.GeneratedLut, Is.True);
                Assert.That(AssetDatabase.GetAssetPath(previewMaterial.GetTexture("_PaletteColor")), Is.EqualTo(AssetDatabase.GetAssetPath(colors.GeneratedLut)));
                Assert.That(previewMaterial.shader.name, Is.EqualTo("Voxel Bridge/VoxelWorldOpaque"));
                Assert.That(previewMaterial.GetFloat("_EmissionIntensity"), Is.EqualTo(1f));
            }
            finally
            {
                window.DiscardChanges(); UnityEngine.Object.DestroyImmediate(window);
                if (restored != null) { restored.DiscardChanges(); UnityEngine.Object.DestroyImmediate(restored); }
            }
        }

        [Test]
        public void Preview_RendersSemanticMeshInHdrp()
        {
            Create(8);
            Assert.That(VoxelPaletteLutGenerator.TryRebuild(colors, out _, out string error), Is.True, error);
            Assert.That(VoxelPaletteLutGenerator.TryRebuild(surfaces, out _, out error), Is.True, error);
            AssetDatabase.ImportAsset(sidecarPath, ImportAssetOptions.ForceSynchronousImport);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var window = ScriptableObject.CreateInstance<VoxelSurfacePainterWindow>();
            Texture2D capture = null;
            try
            {
                var data = new SerializedObject(window);
                data.FindProperty("sourceGuid").stringValue = AssetDatabase.AssetPathToGUID(path);
                data.ApplyModifiedPropertiesWithoutUndo(); InvokeWindow(window, "LoadSource", false);
                var edit = (VoxelSurfaceEdit)WindowField(window, "edit");
                Assert.That(edit, Is.Not.Null, (string)WindowField(window, "status"));
                InvokeWindow(window, "Change", (Action)(() => edit.Apply(new[] { 0, 1, 2 }, 13)));
                var preview = (PreviewRenderUtility)WindowField(window, "preview");
                var mesh = (Mesh)WindowField(window, "mesh");
                var material = (Material)WindowField(window, "material");
                var camera = preview.camera;
                Vector3 center = mesh.bounds.center;
                camera.transform.SetPositionAndRotation(center + new Vector3(0, .13f, -.35f), Quaternion.Euler(20, 0, 0));
                camera.nearClipPlane = .001f; camera.farClipPlane = 10; camera.aspect = 1.5f;
                float[] lightIntensities = preview.lights.Select(light => light.intensity).ToArray();
                for (int frame = 0; frame < 3; frame++)
                {
                    if (capture != null) UnityEngine.Object.DestroyImmediate(capture);
                    preview.BeginStaticPreview(new Rect(0, 0, 384, 256));
                    preview.DrawMesh(mesh, Matrix4x4.identity, material, 0);
                    preview.Render(true, false);
                    capture = preview.EndStaticPreview();
                    CollectionAssert.AreEqual(lightIntensities, preview.lights.Select(light => light.intensity).ToArray(),
                        "HDRP must not replace the authoring light intensities when initializing its light data.");
                    Assert.That(capture.GetPixels32().Count(pixel => pixel.r > 250 && pixel.g > 250 && pixel.b > 250),
                        Is.LessThan(capture.width * capture.height / 100), "The colored fixture must not become a clipped white silhouette on subsequent renders.");
                }
                Assert.That(capture, Is.Not.Null);
                Assert.That(capture.GetPixels32().Distinct().Count(), Is.GreaterThan(8), "The preview must contain rendered geometry, not only a solid clear color.");
                Directory.CreateDirectory("Logs");
                File.WriteAllBytes("Logs/surface-painter-preview.png", capture.EncodeToPNG());
            }
            finally
            {
                if (capture != null) UnityEngine.Object.DestroyImmediate(capture);
                window.DiscardChanges(); UnityEngine.Object.DestroyImmediate(window);
            }
        }

        [TestCase(0f)]
        [TestCase(0.25f)]
        [TestCase(1f)]
        [TestCase(500f)]
        public void Preview_NormalizesHdrEmissionWithoutChangingSourceMaterial(float intensity)
        {
            colors.MutableEntries[1] = new VoxelColorDefinition(1, "Signal Red", new Color(1f, .18f, .18f));
            Create(8);
            var edit = new VoxelSurfaceEdit(path);
            edit.Apply(Enumerable.Range(0, 8).ToArray(), 13);
            edit.BuildOutput(out var sourceBytes, out var sidecarBytes);
            File.WriteAllBytes(path, sourceBytes); File.WriteAllBytes(sidecarPath, sidecarBytes);
            Assert.That(VoxelPaletteLutGenerator.TryRebuild(colors, out _, out string error), Is.True, error);
            Assert.That(VoxelPaletteLutGenerator.TryRebuild(surfaces, out _, out error), Is.True, error);
            AssetDatabase.ImportAsset(sidecarPath, ImportAssetOptions.ForceSynchronousImport);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var previousScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            var testScene = UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,
                UnityEditor.SceneManagement.NewSceneMode.Additive);
            VoxelSurfacePainterWindow window = null;
            Texture2D capture = null;
            string materialPath = null;
            try
            {
                UnityEngine.SceneManagement.SceneManager.SetActiveScene(testScene);
                GameObject prefab = VoxelProductionExporter.Export(path, folder + "/Production");
                Material original = prefab.GetComponent<MeshRenderer>().sharedMaterial;
                materialPath = AssetDatabase.GetAssetPath(original);
                byte[] savedMaterial = File.ReadAllBytes(materialPath);
                original.SetFloat("_EmissionIntensity", intensity);
                window = ScriptableObject.CreateInstance<VoxelSurfacePainterWindow>();
                var data = new SerializedObject(window);
                data.FindProperty("sourceGuid").stringValue = AssetDatabase.AssetPathToGUID(path);
                data.FindProperty("prefabGuid").stringValue = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(prefab));
                data.ApplyModifiedPropertiesWithoutUndo(); InvokeWindow(window, "LoadSource", false);
                var temporary = (Material)WindowField(window, "material");
                Assert.That(temporary, Is.Not.Null, (string)WindowField(window, "status"));
                Assert.That(AssetDatabase.Contains(temporary), Is.False);
                Assert.That(temporary.GetFloat("_EmissionIntensity"), Is.EqualTo(Mathf.Clamp01(intensity)));
                Assert.That(original.GetFloat("_EmissionIntensity"), Is.EqualTo(intensity));
                CollectionAssert.AreEqual(savedMaterial, File.ReadAllBytes(materialPath));
                CollectionAssert.AreEqual(sourceBytes, File.ReadAllBytes(path));
                CollectionAssert.AreEqual(sidecarBytes, File.ReadAllBytes(sidecarPath));
                Assert.That(window.hasUnsavedChanges, Is.False);
                var mesh = (Mesh)WindowField(window, "mesh");
                Assert.That(mesh.uv.All(uv => uv.x == 1), Is.True);
                Assert.That(mesh.uv4.All(uv => uv.x == 13), Is.True);
                if (intensity > 1)
                {
                    var preview = (PreviewRenderUtility)WindowField(window, "preview");
                    var camera = preview.camera;
                    camera.transform.SetPositionAndRotation(mesh.bounds.center + new Vector3(0, .13f, -.35f), Quaternion.Euler(20, 0, 0));
                    camera.nearClipPlane = .001f; camera.farClipPlane = 10; camera.aspect = 1.5f;
                    for (int frame = 0; frame < 3; frame++)
                    {
                        if (capture != null) UnityEngine.Object.DestroyImmediate(capture);
                        preview.BeginStaticPreview(new Rect(0, 0, 384, 256));
                        preview.DrawMesh(mesh, Matrix4x4.identity, temporary, 0);
                        preview.Render(true, false);
                        capture = preview.EndStaticPreview();
                    }
                    Color32[] pixels = capture.GetPixels32();
                    Assert.That(pixels.Count(pixel => pixel.r > 150 && pixel.g < 150 && pixel.b < 150), Is.GreaterThan(100),
                        "The red emissive fixture must remain red instead of clipping all channels to white.");
                    Directory.CreateDirectory("Logs");
                    File.WriteAllBytes("Logs/surface-preview-red-emission.png", capture.EncodeToPNG());
                }
            }
            finally
            {
                if (capture != null) UnityEngine.Object.DestroyImmediate(capture);
                if (window != null) { window.DiscardChanges(); UnityEngine.Object.DestroyImmediate(window); }
                if (previousScene.IsValid() && previousScene.isLoaded) UnityEngine.SceneManagement.SceneManager.SetActiveScene(previousScene);
                UnityEditor.SceneManagement.EditorSceneManager.CloseScene(testScene, true);
                if (materialPath != null) AssetDatabase.DeleteAsset(materialPath);
            }
        }

        private static object WindowField(VoxelSurfacePainterWindow window, string name) =>
            typeof(VoxelSurfacePainterWindow).GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(window);

        private static object InvokeWindow(VoxelSurfacePainterWindow window, string name, params object[] args) =>
            typeof(VoxelSurfacePainterWindow).GetMethod(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).Invoke(window, args);

        [TestCase(false)]
        [TestCase(true)]
        public void SavedSurfaceEdit_RebuildsLinkedPrefabWithoutChangingInstanceOrAssetIdentity(bool saveFirst)
        {
            Create(8);
            Assert.That(VoxelPaletteLutGenerator.TryRebuild(colors, out _, out string error), Is.True, error);
            Assert.That(VoxelPaletteLutGenerator.TryRebuild(surfaces, out _, out error), Is.True, error);
            AssetDatabase.ImportAsset(sidecarPath, ImportAssetOptions.ForceSynchronousImport);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var previousScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            var testScene = UnityEditor.SceneManagement.EditorSceneManager.NewScene(UnityEditor.SceneManagement.NewSceneSetup.EmptyScene,
                UnityEditor.SceneManagement.NewSceneMode.Additive);
            string sharedMaterialPath = null;
            VoxelSurfacePainterWindow window = null;
            try
            {
                UnityEngine.SceneManagement.SceneManager.SetActiveScene(testScene);
                GameObject prefab = VoxelProductionExporter.Export(path, folder + "/Production");
                string prefabPath = AssetDatabase.GetAssetPath(prefab);
                Mesh mesh = prefab.GetComponent<MeshFilter>().sharedMesh;
                Material material = prefab.GetComponent<MeshRenderer>().sharedMaterial;
                sharedMaterialPath = AssetDatabase.GetAssetPath(material);
                string meshGuid = AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(mesh));
                var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                instance.transform.position = new Vector3(13, 4, -20);
                window = ScriptableObject.CreateInstance<VoxelSurfacePainterWindow>();
                var state = new SerializedObject(window);
                state.FindProperty("sourceGuid").stringValue = AssetDatabase.AssetPathToGUID(path);
                state.FindProperty("prefabGuid").stringValue = AssetDatabase.AssetPathToGUID(prefabPath);
                state.ApplyModifiedPropertiesWithoutUndo(); InvokeWindow(window, "LoadSource", false);
                var edit = (VoxelSurfaceEdit)WindowField(window, "edit");
                InvokeWindow(window, "Change", (Action)(() => edit.Apply(new[] { 0 }, 7)));
                if (saveFirst)
                {
                    InvokeWindow(window, "SaveSource");
                    Assert.That(mesh.uv4.All(uv => uv.x == 0), Is.True, "Saving only must not silently rebuild the mesh.");
                }
                InvokeWindow(window, "SaveAndRebuild");
                Assert.That(window.hasUnsavedChanges, Is.False);
                Assert.That(instance.transform.position, Is.EqualTo(new Vector3(13, 4, -20)));
                Assert.That(AssetDatabase.AssetPathToGUID(AssetDatabase.GetAssetPath(instance.GetComponent<MeshFilter>().sharedMesh)), Is.EqualTo(meshGuid));
                Assert.That(instance.GetComponent<MeshRenderer>().sharedMaterial == material, Is.True);
                Assert.That(instance.GetComponent<MeshFilter>().sharedMesh.uv4.Any(uv => uv.x == 7), Is.True);
                Assert.That(instance.GetComponent<MeshFilter>().sharedMesh.uv.All(uv => uv.x == 1), Is.True);
            }
            finally
            {
                if (window != null) { window.DiscardChanges(); UnityEngine.Object.DestroyImmediate(window); }
                if (previousScene.IsValid() && previousScene.isLoaded) UnityEngine.SceneManagement.SceneManager.SetActiveScene(previousScene);
                UnityEditor.SceneManagement.EditorSceneManager.CloseScene(testScene, true);
                if (sharedMaterialPath != null) AssetDatabase.DeleteAsset(sharedMaterialPath);
            }
        }

        [Test]
        public void PrefabDrop_RejectsNonProductionAssets()
        {
            Create(8);
            Assert.Throws<ArgumentException>(() => VoxelSurfacePainterWindow.PrefabLevels(path));
            Assert.Throws<ArgumentException>(() => VoxelSurfacePainterWindow.PrefabLevels(folder + "/Missing.prefab"));
        }

        [TestCase(false)]
        [TestCase(true)]
        public void PrefabDrop_PreservesDraftAndRebuildsChosenLevel(bool family)
        {
            Create(8);
            Assert.That(VoxelPaletteLutGenerator.TryRebuild(colors, out _, out string error), Is.True, error);
            Assert.That(VoxelPaletteLutGenerator.TryRebuild(surfaces, out _, out error), Is.True, error);
            AssetDatabase.ImportAsset(sidecarPath, ImportAssetOptions.ForceSynchronousImport);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var window = ScriptableObject.CreateInstance<VoxelSurfacePainterWindow>();
            string sharedMaterialPath = null;
            try
            {
                string prefabPath;
                if (family)
                {
                    var profile = ScriptableObject.CreateInstance<VoxelStyleProfile>();
                    AssetDatabase.CreateAsset(profile, folder + "/Profile.asset");
                    string manifestPath = VoxelProductionFamily.Create(path, profile, folder + "/Production");
                    VoxelProductionFamily.DeriveLevel(manifestPath, 1, VoxelLodGenerationMode.DuplicateParent);
                    prefabPath = VoxelProductionFamily.Load(manifestPath).prefabAssetPath;
                }
                else prefabPath = AssetDatabase.GetAssetPath(VoxelProductionExporter.Export(path, folder + "/Production"));
                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                sharedMaterialPath = AssetDatabase.GetAssetPath(prefab.GetComponentInChildren<MeshRenderer>().sharedMaterial);
                string prefabGuid = AssetDatabase.AssetPathToGUID(prefabPath);
                var entries = VoxelSurfacePainterWindow.PrefabLevels(prefabPath);
                CollectionAssert.AreEqual(family ? new[] { 0, 1 } : new[] { 0 }, entries.Select(e => e.lodIndex));
                Assert.That(entries[0].sourceGuid, Is.EqualTo(AssetDatabase.AssetPathToGUID(path)));

                InvokeWindow(window, "SetSource", path, null, 0);
                var edit = (VoxelSurfaceEdit)WindowField(window, "edit");
                InvokeWindow(window, "Change", (Action)(() => edit.Select(new[] { 0 })));
                InvokeWindow(window, "Change", (Action)(() => edit.Apply(new[] { 0 }, 7)));
                string draft = EditorJsonUtility.ToJson(window);
                byte[] original = File.ReadAllBytes(path);
                Assert.That(InvokeWindow(window, "PrefabSourceMenu", prefabPath), Is.Not.Null);
                Assert.That(EditorJsonUtility.ToJson(window), Is.EqualTo(draft), "Opening or dismissing the LOD menu must not replace the current session.");
                Assert.That(WindowField(window, "edit"), Is.SameAs(edit));
                CollectionAssert.AreEqual(original, File.ReadAllBytes(path));

                Assert.That(AssetDatabase.CopyAsset(prefabPath, folder + "/Copied.prefab"), Is.True);
                Assert.Throws<InvalidDataException>(() => VoxelSurfacePainterWindow.PrefabLevels(folder + "/Copied.prefab"));
                string moved = folder + "/Moved.prefab";
                Assert.That(AssetDatabase.MoveAsset(prefabPath, moved), Is.Empty);
                prefabPath = moved;
                InvokeWindow(window, "OpenPrefabLevel", prefabGuid, 0, entries[0].sourceGuid);
                Assert.That(WindowField(window, "prefabGuid"), Is.EqualTo(prefabGuid));
                Assert.That(WindowField(window, "edit"), Is.SameAs(edit), "Relinking the current source must preserve its edit buffer.");
                Assert.That(edit.HistorySteps, Is.EqualTo(2)); Assert.That(edit.PendingCells, Is.EqualTo(1));
                CollectionAssert.AreEquivalent(new[] { 0 }, (HashSet<int>)WindowField(window, "selected"));
                InvokeWindow(window, "SaveAndRebuild");
                Assert.That(WindowField(window, "statusType"), Is.EqualTo(MessageType.Info), (string)WindowField(window, "status"));
                Assert.That(window.hasUnsavedChanges, Is.False);
                Assert.That(new VoxelSurfaceEdit(path).Grid.SemanticIds[0], Is.EqualTo(VoxelSemanticEncoding.Pack(1, 7)));

                if (family)
                {
                    byte[] savedBase = File.ReadAllBytes(path);
                    string secondPath = VoxelProductionFamily.SourcePath(entries[1]);
                    InvokeWindow(window, "OpenPrefabLevel", prefabGuid, 1, entries[1].sourceGuid);
                    Assert.That(WindowField(window, "level"), Is.EqualTo(1));
                    Assert.That(WindowField(window, "sourceGuid"), Is.EqualTo(entries[1].sourceGuid));
                    edit = (VoxelSurfaceEdit)WindowField(window, "edit");
                    InvokeWindow(window, "Change", (Action)(() => edit.Apply(new[] { 0 }, 13)));
                    InvokeWindow(window, "SaveAndRebuild");
                    Assert.That(window.hasUnsavedChanges, Is.False);
                    CollectionAssert.AreEqual(savedBase, File.ReadAllBytes(path), "Rebuilding LOD1 must not overwrite LOD0.");
                    Assert.That(new VoxelSurfaceEdit(secondPath).Grid.SemanticIds[0], Is.EqualTo(VoxelSemanticEncoding.Pack(1, 13)));
                    var manifest = VoxelProductionFamily.Load(AssetDatabase.GUIDToAssetPath(VoxelProductionLink.Load(prefabPath).manifestGuid));
                    var mesh = AssetDatabase.LoadAssetAtPath<Mesh>(AssetDatabase.GUIDToAssetPath(manifest.lods[1].meshGuids[0]));
                    Assert.That(mesh.uv4.Any(uv => uv.x == 13), Is.True);
                    edit = (VoxelSurfaceEdit)WindowField(window, "edit");
                    Assert.That(AssetDatabase.DeleteAsset(secondPath), Is.True);
                    Assert.That(InvokeWindow(window, "PrefabSourceMenu", prefabPath), Is.Not.Null, "Missing sources remain listed but cannot be opened.");
                    Assert.Throws<System.Reflection.TargetInvocationException>(() => InvokeWindow(window, "OpenPrefabLevel", prefabGuid, 1, entries[1].sourceGuid));
                    Assert.That(WindowField(window, "edit"), Is.SameAs(edit), "The saved buffer remains loaded after a rejected source change.");
                }
                string currentGuid = (string)WindowField(window, "sourceGuid");
                Assert.Throws<System.Reflection.TargetInvocationException>(() => InvokeWindow(window, "OpenPrefabLevel", prefabGuid, 0, "changed-source-guid"));
                Assert.That(WindowField(window, "sourceGuid"), Is.EqualTo(currentGuid));
            }
            finally
            {
                window.DiscardChanges(); UnityEngine.Object.DestroyImmediate(window);
                if (sharedMaterialPath != null) AssetDatabase.DeleteAsset(sharedMaterialPath);
            }
        }

        [Test]
        public void CameraNavigation_AllowsCellScaleZoomAndFramesSelectedCells()
        {
            var grid = new VoxelGrid(new Vector3Int(50, 20, 10), new Vector3(-2, 3, 5), .032f, true);
            int cell = grid.Index(40, 15, 5); grid.Occupied[cell] = true;
            VoxelSurfacePainterWindow.SelectionFrame(grid, new[] { cell }, out Vector3 pivot, out float distance);
            Assert.That(pivot, Is.EqualTo(grid.Origin + new Vector3(40.5f, 15.5f, 5.5f) * grid.VoxelSize));
            Assert.That(distance, Is.InRange(grid.VoxelSize * 2, grid.VoxelSize * 4));
            float zoom = 4;
            for (int i = 0; i < 30; i++) zoom = VoxelSurfacePainterWindow.ZoomDistance(zoom, -5, grid.VoxelSize, 1);
            Assert.That(zoom, Is.EqualTo(grid.VoxelSize * 2).Within(.00001f));
            Assert.That(zoom, Is.LessThan(1.05f), "Zoom must not be clamped to the radius of the entire model.");
            Assert.That(VoxelSurfacePainterWindow.ZoomDistance(100, 100, grid.VoxelSize, 1), Is.EqualTo(100));
            Assert.That(VoxelSurfacePainterWindow.RotateView(Vector2.zero, new Vector2(10, 4)), Is.EqualTo(new Vector2(2, 5)));
            Vector2 clamped = VoxelSurfacePainterWindow.RotateView(new Vector2(88, 179), new Vector2(10, 10));
            Assert.That(clamped.x, Is.EqualTo(89)); Assert.That(clamped.y, Is.EqualTo(-176));
            Assert.Throws<ArgumentException>(() => VoxelSurfacePainterWindow.SelectionFrame(grid, Array.Empty<int>(), out _, out _));
        }

        [Test]
        public void ContextShortcuts_UseLocalHistoryAndLeaveTextEditingAlone()
        {
            Create(8);
            Assert.That(VoxelPaletteLutGenerator.TryRebuild(colors, out _, out string error), Is.True, error);
            Assert.That(VoxelPaletteLutGenerator.TryRebuild(surfaces, out _, out error), Is.True, error);
            AssetDatabase.ImportAsset(sidecarPath, ImportAssetOptions.ForceSynchronousImport);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var window = ScriptableObject.CreateInstance<VoxelSurfacePainterWindow>();
            bool editingText = EditorGUIUtility.editingTextField;
            try
            {
                var state = new SerializedObject(window);
                state.FindProperty("sourceGuid").stringValue = AssetDatabase.AssetPathToGUID(path);
                state.ApplyModifiedPropertiesWithoutUndo(); InvokeWindow(window, "LoadSource", false);
                var edit = (VoxelSurfaceEdit)WindowField(window, "edit");
                InvokeWindow(window, "Change", (Action)(() => edit.Apply(new[] { 0 }, 7)));
                EditorGUIUtility.editingTextField = false;
                int undoGroup = Undo.GetCurrentGroup();
                var arguments = new UnityEditor.ShortcutManagement.ShortcutArguments { context = window };
                var flags = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic;
                foreach (string method in new[] { "UndoShortcut", "RedoShortcut", "UndoShortcut", "RedoAlternateShortcut" })
                {
                    typeof(VoxelSurfacePainterWindow).GetMethod(method, flags).Invoke(null, new object[] { arguments });
                    Assert.That(edit.PendingCells, Is.EqualTo(method == "UndoShortcut" ? 0 : 1));
                }
                Assert.That(Undo.GetCurrentGroup(), Is.EqualTo(undoGroup));
                EditorGUIUtility.editingTextField = true;
                typeof(VoxelSurfacePainterWindow).GetMethod("UndoShortcut", flags).Invoke(null, new object[] { arguments });
                Assert.That(edit.PendingCells, Is.EqualTo(1));
                EditorGUIUtility.editingTextField = false;
                arguments.context = new object();
                typeof(VoxelSurfacePainterWindow).GetMethod("UndoShortcut", flags).Invoke(null, new object[] { arguments });
                Assert.That(edit.PendingCells, Is.EqualTo(1), "A different shortcut context must not edit this window.");
                arguments.context = window;
                InvokeWindow(window, "Change", (Action)(() => edit.Select(new[] { 0, 1 })));
                var selected = (HashSet<int>)WindowField(window, "selected");
                foreach (string method in new[] { "UndoShortcut", "RedoShortcut", "UndoShortcut", "RedoAlternateShortcut" })
                {
                    typeof(VoxelSurfacePainterWindow).GetMethod(method, flags).Invoke(null, new object[] { arguments });
                    Assert.That(selected.Count, Is.EqualTo(method == "UndoShortcut" ? 0 : 2));
                    Assert.That(edit.PendingCells, Is.EqualTo(1));
                    Assert.That(window.hasUnsavedChanges, Is.True, "Selection undo must preserve the pending surface draft.");
                }
                EditorGUIUtility.editingTextField = true;
                typeof(VoxelSurfacePainterWindow).GetMethod("UndoShortcut", flags).Invoke(null, new object[] { arguments });
                Assert.That(selected.Count, Is.EqualTo(2));
                EditorGUIUtility.editingTextField = false;
                var preview = (PreviewRenderUtility)WindowField(window, "preview");
                var job = new VoxelSurfaceSelection(edit.Grid, preview.camera, new Vector2(64, 64), VoxelSelectionMode.Replace, selected);
                typeof(VoxelSurfacePainterWindow).GetField("selectionJob", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(window, job);
                typeof(VoxelSurfacePainterWindow).GetMethod("UndoShortcut", flags).Invoke(null, new object[] { arguments });
                Assert.That(selected.Count, Is.EqualTo(2), "History is locked while a gesture is pending.");
                InvokeWindow(window, "CancelSelection");
                Assert.That(edit.HistorySteps, Is.EqualTo(2), "Cancelling a gesture must not record a history step.");
                typeof(VoxelSurfacePainterWindow).GetMethod("UndoShortcut", flags).Invoke(null, new object[] { arguments });
                Assert.That(selected, Is.Empty);
                Assert.That(Undo.GetCurrentGroup(), Is.EqualTo(undoGroup));
            }
            finally
            {
                EditorGUIUtility.editingTextField = editingText;
                window.DiscardChanges(); UnityEngine.Object.DestroyImmediate(window);
            }
        }

        [Test]
        public void OverlayCache_RefreshesSameSizeReplacementAndReleasesTemporaryMeshes()
        {
            Create(8);
            Assert.That(VoxelPaletteLutGenerator.TryRebuild(colors, out _, out string error), Is.True, error);
            Assert.That(VoxelPaletteLutGenerator.TryRebuild(surfaces, out _, out error), Is.True, error);
            AssetDatabase.ImportAsset(sidecarPath, ImportAssetOptions.ForceSynchronousImport);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var window = ScriptableObject.CreateInstance<VoxelSurfacePainterWindow>();
            try
            {
                var state = new SerializedObject(window);
                state.FindProperty("sourceGuid").stringValue = AssetDatabase.AssetPathToGUID(path);
                state.ApplyModifiedPropertiesWithoutUndo(); InvokeWindow(window, "LoadSource", false);
                var selected = (HashSet<int>)WindowField(window, "selected");
                selected.Add(0); InvokeWindow(window, "UpdateSelectionOverlay");
                var meshes = (List<Mesh>)WindowField(window, "selectionMeshes");
                Mesh original = meshes[0];
                InvokeWindow(window, "UpdateSelectionOverlay");
                Assert.That(meshes[0], Is.SameAs(original), "Unchanged selections must reuse their temporary geometry.");
                var edit = (VoxelSurfaceEdit)WindowField(window, "edit");
                var preview = (PreviewRenderUtility)WindowField(window, "preview");
                var job = new VoxelSurfaceSelection(edit.Grid, preview.camera, new Vector2(64, 64), VoxelSelectionMode.Add, new[] { 1 });
                typeof(VoxelSurfacePainterWindow).GetField("selectionJob", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).SetValue(window, job);
                InvokeWindow(window, "ProcessSelection");
                CollectionAssert.AreEquivalent(new[] { 1 }, selected);
                Assert.That(edit.HistorySteps, Is.EqualTo(1), "A completed gesture records exactly one step.");
                InvokeWindow(window, "UpdateSelectionOverlay");
                Assert.That(original == null, Is.True, "A same-count replacement must destroy the stale face geometry.");
                Mesh replacement = meshes[0];
                Mesh modelMesh = (Mesh)WindowField(window, "mesh");
                InvokeWindow(window, "Change", (Action)edit.Undo);
                CollectionAssert.AreEquivalent(new[] { 0 }, selected);
                InvokeWindow(window, "UpdateSelectionOverlay");
                Assert.That(replacement == null, Is.True, "Undo must invalidate same-count selection geometry.");
                replacement = meshes[0];
                InvokeWindow(window, "Change", (Action)edit.Redo);
                CollectionAssert.AreEquivalent(new[] { 1 }, selected);
                InvokeWindow(window, "UpdateSelectionOverlay");
                Assert.That(replacement == null, Is.True);
                Assert.That(WindowField(window, "mesh"), Is.SameAs(modelMesh), "Selection history must not remesh the model.");
                replacement = meshes[0];
                Material overlayMaterial = (Material)WindowField(window, "selectionMaterial");
                InvokeWindow(window, "ReleasePreview");
                Assert.That(meshes, Is.Empty);
                Assert.That(replacement == null && overlayMaterial == null, Is.True);
                Assert.That(window.hasUnsavedChanges, Is.False);
            }
            finally { window.DiscardChanges(); UnityEngine.Object.DestroyImmediate(window); }
        }

        [Test]
        public void Eyedropper_ReadsBothIdsAndChangesOnlyTheTargetSurface()
        {
            Create(8);
            Assert.That(VoxelPaletteLutGenerator.TryRebuild(colors, out _, out string error), Is.True, error);
            Assert.That(VoxelPaletteLutGenerator.TryRebuild(surfaces, out _, out error), Is.True, error);
            AssetDatabase.ImportAsset(sidecarPath, ImportAssetOptions.ForceSynchronousImport);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            byte[] beforeVox = File.ReadAllBytes(path), beforeSidecar = File.ReadAllBytes(sidecarPath);
            var window = ScriptableObject.CreateInstance<VoxelSurfacePainterWindow>();
            try
            {
                var state = new SerializedObject(window);
                state.FindProperty("sourceGuid").stringValue = AssetDatabase.AssetPathToGUID(path);
                state.ApplyModifiedPropertiesWithoutUndo(); InvokeWindow(window, "LoadSource", false);
                var edit = (VoxelSurfaceEdit)WindowField(window, "edit");
                var selected = (HashSet<int>)WindowField(window, "selected"); selected.Add(0); selected.Add(1);
                InvokeWindow(window, "Change", (Action)(() => edit.Apply(new[] { 0 }, 7)));
                ushort[] pairs = (ushort[])edit.Grid.SemanticIds.Clone();
                InvokeWindow(window, "SampleCell", 0, true);
                Assert.That(WindowField(window, "sampledCell"), Is.EqualTo(0));
                Assert.That(WindowField(window, "surfaceId"), Is.EqualTo(7));
                InvokeWindow(window, "SampleCell", 1, false);
                Assert.That(WindowField(window, "sampledCell"), Is.EqualTo(1));
                Assert.That(WindowField(window, "surfaceId"), Is.EqualTo(7), "Match sampling must not replace the chosen target surface.");
                InvokeWindow(window, "SampleCell", 1, true);
                Assert.That(WindowField(window, "surfaceId"), Is.EqualTo(0));
                CollectionAssert.AreEquivalent(new[] { 0, 1 }, selected);
                CollectionAssert.AreEqual(pairs, edit.Grid.SemanticIds);
                Assert.That(edit.HistorySteps, Is.EqualTo(1)); Assert.That(edit.PendingCells, Is.EqualTo(1));
                CollectionAssert.AreEqual(beforeVox, File.ReadAllBytes(path));
                CollectionAssert.AreEqual(beforeSidecar, File.ReadAllBytes(sidecarPath));
            }
            finally { window.DiscardChanges(); UnityEngine.Object.DestroyImmediate(window); }
        }

        private sealed class PointerCaptureWindow : EditorWindow
        {
            internal VoxelSurfacePainterWindow Painter;
            internal Rect Viewport;
            internal Vector2 Pointer;
            internal Exception Error;
            internal int Repaints;

            private void OnGUI()
            {
                if (Event.current.type != EventType.Repaint || Painter == null) return;
                Repaints++;
                Vector2 previous = Event.current.mousePosition;
                try
                {
                    Event.current.mousePosition = Pointer;
                    InvokeWindow(Painter, "DrawPreview", Viewport);
                }
                catch (Exception exception) { Error = exception; }
                finally { Event.current.mousePosition = previous; }
            }
        }

        [TestCase(0, 60, 205, 180)]
        [TestCase(35, 90, 205, 180)]
        [TestCase(35, 90, 205, 120)]
        public void BrushCursor_MatchesPointerWithOffsetViewport(float x, float y, float pointerX, float pointerY)
        {
            Create(8);
            Assert.That(VoxelPaletteLutGenerator.TryRebuild(colors, out _, out string error), Is.True, error);
            Assert.That(VoxelPaletteLutGenerator.TryRebuild(surfaces, out _, out error), Is.True, error);
            AssetDatabase.ImportAsset(sidecarPath, ImportAssetOptions.ForceSynchronousImport);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);
            var painter = ScriptableObject.CreateInstance<VoxelSurfacePainterWindow>();
            var probe = ScriptableObject.CreateInstance<PointerCaptureWindow>();
            var target = new RenderTexture(450, 360, 24);
            var capture = new Texture2D(450, 360, TextureFormat.RGB24, false);
            RenderTexture previous = RenderTexture.active;
            try
            {
                var state = new SerializedObject(painter);
                state.FindProperty("sourceGuid").stringValue = AssetDatabase.AssetPathToGUID(path);
                state.FindProperty("brushDiameter").intValue = 24;
                state.ApplyModifiedPropertiesWithoutUndo(); InvokeWindow(painter, "LoadSource", false);
                probe.Painter = painter; probe.Viewport = new Rect(x, y, 360, 230); probe.Pointer = new Vector2(pointerX, pointerY);
                probe.ShowUtility(); probe.position = new Rect(100, 100, 450, 360);
                probe.SendEvent(new Event { type = EventType.Repaint });
                target.Create();
                var method = typeof(UnityEditorInternal.InternalEditorUtility).GetMethod("CaptureEditorWindow",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                Assert.That((bool)method.Invoke(null, new object[] { probe, target }), Is.True);
                Assert.That(probe.Error, Is.Null);
                Assert.That(probe.Repaints, Is.GreaterThan(0), "The native window must render before evaluating cursor pixels.");
                RenderTexture.active = target; capture.ReadPixels(new Rect(0, 0, 450, 360), 0, 0); capture.Apply();
                File.WriteAllBytes("Logs/brush-cursor-regression.png", capture.EncodeToPNG());
                var pixels = capture.GetPixels32(); var cyan = new List<Vector2>();
                for (int py = 0; py < 360; py++) for (int px = 0; px < 450; px++)
                {
                    var p = pixels[py * 450 + px];
                    // Handles blends its cyan stroke with the viewport background.
                    if (p.r < 30 && p.g > 180 && p.b > 180) cyan.Add(new Vector2(px + .5f, 360 - py - .5f));
                }
                Assert.That(cyan.Count, Is.GreaterThan(12), "The cursor must remain visible near the top of an offset viewport.");
                Assert.That(cyan.Average(p => p.x), Is.EqualTo(pointerX).Within(1.5f));
                Assert.That(cyan.Average(p => p.y), Is.EqualTo(pointerY).Within(1.5f));
                Assert.That(cyan.Max(p => p.x) - cyan.Min(p => p.x), Is.InRange(22f, 26f));
                Assert.That(cyan.Max(p => p.y) - cyan.Min(p => p.y), Is.InRange(22f, 26f));
            }
            finally
            {
                RenderTexture.active = previous; target.Release();
                UnityEngine.Object.DestroyImmediate(target); UnityEngine.Object.DestroyImmediate(capture);
                UnityEngine.Object.DestroyImmediate(probe); painter.DiscardChanges(); UnityEngine.Object.DestroyImmediate(painter);
            }
        }

        private VoxelBridgeMetadata Create(int width = 35, bool fullPalette = false)
        {
            var grid = new VoxelGrid(new Vector3Int(width, 2, 2), new Vector3(-2, 3, 5), .032f, true);
            for (int x = 0; x < width; x++)
            {
                int cell = grid.Index(x, 0, 0);
                grid.Occupied[cell] = true;
                grid.SemanticIds[cell] = VoxelSemanticEncoding.Pack(fullPalette ? x % 255 : 1, 0);
            }
            var quantized = VoxelSemanticQuantizer.Quantize(grid, colors, surfaces);
            var written = VoxelChunkedVoxWriter.Write(path, grid, quantized, 16);
            Assert.That(VoxelSemanticTransport.TryCreateMetadata(quantized.SemanticSlots, colors, surfaces,
                out var semantic, out string error), Is.True, error);
            var metadata = new VoxelBridgeMetadata
            {
                formatVersion = 4, voxelSize = grid.VoxelSize, gridOrigin = grid.Origin,
                unityGridSize = grid.Size, chunks = written.Chunks, semantic = semantic,
                paletteColorCount = quantized.SemanticSlots.Length, sourceName = "Surface fixture", lodIndex = 0
            };
            File.WriteAllText(sidecarPath, JsonUtility.ToJson(metadata, true));
            return metadata;
        }

        private VoxelGrid ReadOutput(byte[] vox, byte[] sidecar)
        {
            string candidate = folder + "/Candidate.vox";
            File.WriteAllBytes(candidate, vox);
            return VoxelVolumeReader.Read(candidate, JsonUtility.FromJson<VoxelBridgeMetadata>(Encoding.UTF8.GetString(sidecar)));
        }

        [Test]
        public void PartialSlotEdit_PreservesColorGeometryAndUnrelatedChunks()
        {
            var metadata = Create();
            Append("NOTE", Data(w => { w.Write(1); w.Write(4); w.Write(Encoding.UTF8.GetBytes("test")); }));
            Append("MATL", Data(w => { w.Write(1); w.Write(0); }));
            byte[] original = File.ReadAllBytes(path), originalMetadata = File.ReadAllBytes(sidecarPath);
            var edit = new VoxelSurfaceEdit(path);
            Assert.That(edit.Apply(new[] { 0, 17, 34, 0 }, 7), Is.EqualTo(3));
            edit.BuildOutput(out var output, out var sidecar);
            VoxelGrid read = ReadOutput(output, sidecar);
            Assert.That(read.CountOccupied(), Is.EqualTo(35));
            Assert.That(read.Origin, Is.EqualTo(metadata.gridOrigin));
            Assert.That(read.VoxelSize, Is.EqualTo(.032f));
            for (int x = 0; x < 35; x++)
                Assert.That(read.SemanticIds[x], Is.EqualTo(VoxelSemanticEncoding.Pack(1, x == 0 || x == 17 || x == 34 ? 7 : 0)));
            var oldChunks = Chunks(original); var newChunks = Chunks(output);
            Assert.That(newChunks.Count, Is.EqualTo(oldChunks.Count));
            for (int i = 0; i < oldChunks.Count; i++)
                if (oldChunks[i].Id != "RGBA" && oldChunks[i].Id != "XYZI")
                    CollectionAssert.AreEqual(oldChunks[i].Bytes, newChunks[i].Bytes);
            var revised = JsonUtility.FromJson<VoxelBridgeMetadata>(Encoding.UTF8.GetString(sidecar));
            Assert.That(revised.semantic.slots.Length, Is.EqualTo(2));
            Assert.That(revised.semantic.slots[0].displayColor, Is.EqualTo(revised.semantic.slots[1].displayColor));
            Assert.That(revised.semantic.slots.Single(s => s.surfaceId == 0).slot, Is.EqualTo(metadata.semantic.slots[0].slot));
            Assert.That(revised.sourceName, Is.EqualTo(metadata.sourceName));
            CollectionAssert.AreEqual(original, File.ReadAllBytes(path));
            CollectionAssert.AreEqual(originalMetadata, File.ReadAllBytes(sidecarPath));
        }

        [Test]
        public void SelectionAndSurfaceHistory_UndoRedoInChronologicalOrder()
        {
            Create();
            var selection = new HashSet<int>();
            var edit = new VoxelSurfaceEdit(path, selection);
            edit.Select(new[] { 0, 1 });
            edit.Apply(selection, 7);
            edit.Select(new[] { 1, 2 });
            edit.Select(Array.Empty<int>());
            Assert.That(edit.HistorySteps, Is.EqualTo(4));
            Assert.That(edit.PendingCells, Is.EqualTo(2));
            edit.Undo(); CollectionAssert.AreEquivalent(new[] { 1, 2 }, selection);
            edit.Undo(); CollectionAssert.AreEquivalent(new[] { 0, 1 }, selection);
            Assert.That(edit.PendingCells, Is.EqualTo(2));
            edit.Undo(); Assert.That(edit.PendingCells, Is.Zero);
            CollectionAssert.AreEquivalent(new[] { 0, 1 }, selection);
            edit.Undo(); Assert.That(selection, Is.Empty);
            edit.BuildOutput(out var vox, out var sidecar);
            CollectionAssert.AreEqual(File.ReadAllBytes(path), vox);
            CollectionAssert.AreEqual(File.ReadAllBytes(sidecarPath), sidecar);
            edit.Redo(); CollectionAssert.AreEquivalent(new[] { 0, 1 }, selection);
            edit.Redo(); Assert.That(edit.PendingCells, Is.EqualTo(2));
            edit.Redo(); CollectionAssert.AreEquivalent(new[] { 1, 2 }, selection);
            edit.Redo(); Assert.That(selection, Is.Empty);
            Assert.That(edit.CanRedo, Is.False);
        }

        [Test]
        public void SelectionHistory_NoOpsAndInvalidInputPreserveRedoAndSource()
        {
            Create();
            var selection = new HashSet<int>();
            var edit = new VoxelSurfaceEdit(path, selection);
            edit.Select(new[] { 0 }); edit.Select(new[] { 1 }); edit.Undo();
            edit.Select(new[] { 0, 0 });
            Assert.Throws<ArgumentNullException>(() => edit.Select(null));
            foreach (int invalid in new[] { -1, 35, 100000 })
                Assert.Throws<InvalidDataException>(() => edit.Select(new[] { 2, invalid }));
            Assert.Throws<InvalidOperationException>(() => edit.Select(
                Enumerable.Repeat(0, VoxelSurfaceEdit.MaximumSelection + 1).ToArray()));
            Assert.That(edit.CanRedo, Is.True);
            Assert.That(edit.HistorySteps, Is.EqualTo(2));
            CollectionAssert.AreEquivalent(new[] { 0 }, selection);
            Assert.That(edit.PendingCells, Is.Zero); Assert.That(edit.SurfaceRevision, Is.Zero);
            edit.BuildOutput(out var vox, out var sidecar);
            CollectionAssert.AreEqual(File.ReadAllBytes(path), vox);
            CollectionAssert.AreEqual(File.ReadAllBytes(sidecarPath), sidecar);
            edit.GetChanges(out var cells, out var ids);
            Assert.That(cells, Is.Empty); Assert.That(ids, Is.Empty);
        }

        [Test]
        public void SelectionHistory_BranchingAndEvictionShareSurfaceHistoryBudget()
        {
            Create();
            var selection = new HashSet<int>();
            var edit = new VoxelSurfaceEdit(path, selection);
            edit.Apply(new[] { 0 }, 7); edit.Undo();
            edit.Select(new[] { 1 }); Assert.That(edit.CanRedo, Is.False);
            edit.Undo(); edit.Apply(new[] { 0 }, 7); Assert.That(edit.CanRedo, Is.False);
            for (int i = 0; i < 80; i++) edit.Select(new[] { i % 2 });
            Assert.That(edit.HistorySteps, Is.EqualTo(VoxelSurfaceEdit.MaximumHistorySteps));
            while (edit.CanUndo) edit.Undo();
            Assert.That(edit.PendingCells, Is.EqualTo(1), "Selection history eviction cannot undo older surface assignments.");
            CollectionAssert.AreEquivalent(new[] { 1 }, selection);
            while (edit.CanRedo) edit.Redo();
            CollectionAssert.AreEquivalent(new[] { 1 }, selection);
            edit.ClearHistory();
            Assert.That(edit.CanUndo || edit.CanRedo, Is.False);
            Assert.That(edit.PendingCells, Is.EqualTo(1));
            CollectionAssert.AreEquivalent(new[] { 1 }, selection);
        }

        [Test]
        public void UndoRedoAndNoOp_KeepExactSourceAndBoundedHistory()
        {
            Create(); var edit = new VoxelSurfaceEdit(path);
            Assert.That(edit.Apply(new[] { 0 }, 0), Is.Zero);
            Assert.That(edit.HistorySteps, Is.Zero);
            edit.Apply(new[] { 0, 1 }, 7);
            edit.Apply(new[] { 1 }, 13);
            Assert.That(edit.PendingCells, Is.EqualTo(2));
            edit.Undo(); Assert.That(edit.Grid.SemanticIds[1], Is.EqualTo(VoxelSemanticEncoding.Pack(1, 7)));
            edit.Redo(); Assert.That(edit.Grid.SemanticIds[1], Is.EqualTo(VoxelSemanticEncoding.Pack(1, 13)));
            edit.Undo(); edit.Undo();
            Assert.That(edit.PendingCells, Is.Zero);
            edit.BuildOutput(out var vox, out var sidecar);
            CollectionAssert.AreEqual(File.ReadAllBytes(path), vox);
            CollectionAssert.AreEqual(File.ReadAllBytes(sidecarPath), sidecar);
            edit.Apply(new[] { 2 }, 1);
            Assert.That(edit.CanRedo, Is.False);
            for (int i = 0; i < 80; i++) edit.Apply(new[] { 2 }, i % 2 == 0 ? 7 : 1);
            Assert.That(edit.HistorySteps, Is.EqualTo(VoxelSurfaceEdit.MaximumHistorySteps));
            while (edit.CanUndo) edit.Undo();
            Assert.That(edit.PendingCells, Is.EqualTo(1), "Discarding old history must not pretend to restore the initial source.");
        }

        [TestCase(-1)]
        [TestCase(100000)]
        [TestCase(35)]
        public void InvalidCell_RejectsWholeOperationWithoutChangingRedo(int invalid)
        {
            Create(); var edit = new VoxelSurfaceEdit(path);
            edit.Apply(new[] { 0 }, 7); edit.Undo();
            Assert.Throws<InvalidDataException>(() => edit.Apply(new[] { 1, invalid }, 7));
            Assert.That(edit.PendingCells, Is.Zero);
            Assert.That(edit.CanRedo, Is.True);
        }

        [Test]
        public void UnknownOrNonOpaqueSurfaceAndOversizedSelection_AreRejected()
        {
            Create();
            surfaces.MutableEntries.Add(new VoxelSurfaceDefinition(20, "Glass", VoxelSurfaceRenderClass.Transparent, 0, 1, 0, 1));
            var edit = new VoxelSurfaceEdit(path);
            Assert.Throws<InvalidDataException>(() => edit.Apply(new[] { 0 }, 255));
            Assert.Throws<InvalidDataException>(() => edit.Apply(new[] { 0 }, 20));
            Assert.Throws<InvalidOperationException>(() => edit.Apply(new int[VoxelSurfaceEdit.MaximumSelection + 1], 7));
            Assert.That(edit.PendingCells, Is.Zero);
        }

        [TestCase("vox")]
        [TestCase("sidecar")]
        [TestCase("palette")]
        public void ExternalChanges_BlockOutputWithoutDiscardingEdits(string target)
        {
            Create(); var edit = new VoxelSurfaceEdit(path); edit.Apply(new[] { 0 }, 7);
            if (target == "palette") surfaces.MutableEntries[0] = new VoxelSurfaceDefinition(0, "Changed", VoxelSurfaceRenderClass.Opaque, 0, .9f, 0, 1);
            else
            {
                string changed = target == "vox" ? path : sidecarPath;
                byte[] data = File.ReadAllBytes(changed); data[data.Length - 2] ^= 1; File.WriteAllBytes(changed, data);
            }
            Assert.Throws<InvalidOperationException>(() => edit.BuildOutput(out _, out _));
            Assert.That(edit.PendingCells, Is.EqualTo(1));
            edit.Undo(); Assert.That(edit.PendingCells, Is.Zero);
        }

        [Test]
        public void PairLimit_Rejects256AndReusesFreedSlotWithoutRenumberingOtherPairs()
        {
            var metadata = Create(256, true);
            var edit = new VoxelSurfaceEdit(path); edit.Apply(new[] { 0 }, 7);
            Assert.Throws<InvalidDataException>(() => edit.BuildOutput(out _, out _));
            Assert.That(edit.PendingCells, Is.EqualTo(1));
            edit.Apply(new[] { 255 }, 7); // No cell uses the old color-0/default pair anymore.
            edit.BuildOutput(out var output, out var sidecar);
            var updated = JsonUtility.FromJson<VoxelBridgeMetadata>(Encoding.UTF8.GetString(sidecar));
            Assert.That(updated.semantic.slots.Length, Is.EqualTo(255));
            foreach (var slot in metadata.semantic.slots.Where(s => s.colorId != 0))
                Assert.That(updated.semantic.slots.Single(s => s.colorId == slot.colorId).slot, Is.EqualTo(slot.slot));
            VoxelGrid read = ReadOutput(output, sidecar);
            Assert.That(read.SemanticIds[0], Is.EqualTo(VoxelSemanticEncoding.Pack(0, 7)));
            Assert.That(read.SemanticIds[255], Is.EqualTo(read.SemanticIds[0]));
        }

        [Test]
        public void ExistingPair_IsReusedAndOutputIsDeterministic()
        {
            Create(); var first = new VoxelSurfaceEdit(path); var second = new VoxelSurfaceEdit(path);
            first.Apply(new[] { 0, 17, 34 }, 7);
            second.Apply(new[] { 34 }, 7); second.Apply(new[] { 17, 0 }, 7);
            first.BuildOutput(out var a, out var am); second.BuildOutput(out var b, out var bm);
            CollectionAssert.AreEqual(a, b); CollectionAssert.AreEqual(am, bm);
            File.WriteAllBytes(path, a); File.WriteAllBytes(sidecarPath, am);
            var third = new VoxelSurfaceEdit(path); third.Apply(new[] { 1 }, 7);
            third.BuildOutput(out _, out var cm);
            Assert.That(JsonUtility.FromJson<VoxelBridgeMetadata>(Encoding.UTF8.GetString(cm)).semantic.slots.Length, Is.EqualTo(2));
        }

        [Test]
        public void GridTampering_IsRejected()
        {
            Create(); var edit = new VoxelSurfaceEdit(path);
            edit.Grid.Occupied[0] = false;
            Assert.Throws<InvalidDataException>(() => edit.BuildOutput(out _, out _));
            edit.Grid.Occupied[0] = true; edit.Grid.SemanticIds[0] = VoxelSemanticEncoding.Pack(2, 0);
            Assert.Throws<InvalidDataException>(() => edit.BuildOutput(out _, out _));
        }

        [Test]
        public void ReorderedModels_UseResolvedRecordOffsets()
        {
            Create();
            var chunks = Chunks(File.ReadAllBytes(path));
            var geometry = new List<(string Id, byte[] Bytes)>();
            foreach (var chunk in chunks.Where(c => c.Id == "SIZE" || c.Id == "XYZI")) geometry.Add(chunk);
            var reordered = new List<(string Id, byte[] Bytes)>();
            int modelCount = geometry.Count / 2;
            for (int i = modelCount - 1; i >= 0; i--) { reordered.Add(geometry[i * 2]); reordered.Add(geometry[i * 2 + 1]); }
            foreach (var chunk in chunks.Where(c => c.Id != "SIZE" && c.Id != "XYZI"))
            {
                if (chunk.Id == "nSHP")
                {
                    // Writer emits node ID, empty dict, model count, model ID, empty dict.
                    int model = BitConverter.ToInt32(chunk.Bytes, 24);
                    Array.Copy(BitConverter.GetBytes(modelCount - 1 - model), 0, chunk.Bytes, 24, 4);
                }
                reordered.Add(chunk);
            }
            WriteChunks(reordered);
            var edit = new VoxelSurfaceEdit(path); edit.Apply(new[] { 0 }, 7);
            edit.BuildOutput(out var vox, out var sidecar);
            VoxelGrid read = ReadOutput(vox, sidecar);
            Assert.That(read.SemanticIds[0], Is.EqualTo(VoxelSemanticEncoding.Pack(1, 7)));
            Assert.That(read.SemanticIds[34], Is.EqualTo(VoxelSemanticEncoding.Pack(1, 0)));
        }

        [Test]
        public void DuplicateOccupiedRecords_AreRejectedForEditing()
        {
            Create(); var chunks = Chunks(File.ReadAllBytes(path));
            byte[] xyzi = chunks.First(c => c.Id == "XYZI").Bytes;
            Array.Copy(xyzi, 16, xyzi, 20, 4);
            WriteChunks(chunks);
            Assert.Throws<InvalidDataException>(() => new VoxelSurfaceEdit(path));
        }

        private static byte[] Data(Action<BinaryWriter> write)
        {
            using var buffer = new MemoryStream();
            using (var writer = new BinaryWriter(buffer, Encoding.UTF8, true)) write(writer);
            return buffer.ToArray();
        }

        private static List<(string Id, byte[] Bytes)> Chunks(byte[] vox)
        {
            var result = new List<(string, byte[])>();
            int offset = 20 + BitConverter.ToInt32(vox, 12);
            while (offset < vox.Length)
            {
                int size = 12 + BitConverter.ToInt32(vox, offset + 4) + BitConverter.ToInt32(vox, offset + 8);
                result.Add((Encoding.ASCII.GetString(vox, offset, 4), vox.Skip(offset).Take(size).ToArray())); offset += size;
            }
            return result;
        }

        private void WriteChunks(List<(string Id, byte[] Bytes)> chunks)
        {
            byte[] bytes = Data(w =>
            {
                w.Write(Encoding.ASCII.GetBytes("VOX ")); w.Write(200);
                w.Write(Encoding.ASCII.GetBytes("MAIN")); w.Write(0); w.Write(chunks.Sum(c => c.Bytes.Length));
                foreach (var chunk in chunks) w.Write(chunk.Bytes);
            });
            File.WriteAllBytes(path, bytes);
        }

        private void Append(string id, byte[] content)
        {
            var chunks = Chunks(File.ReadAllBytes(path));
            chunks.Add((id, Data(w => { w.Write(Encoding.ASCII.GetBytes(id)); w.Write(content.Length); w.Write(0); w.Write(content); })));
            WriteChunks(chunks);
        }
    }
}
