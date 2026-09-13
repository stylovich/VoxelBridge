using System;
using System.IO;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using Object = UnityEngine.Object;

namespace LocalModels.VoxelBridge.Tests
{
    public class VoxelLodTransitionUpdaterTests
    {
        private GameObject root;
        private VoxelStyleProfile profile;
        private LODGroup group;
        private MeshRenderer renderer;
        private VoxelLodSetManifest manifest;

        [SetUp]
        public void SetUp()
        {
            profile = ScriptableObject.CreateInstance<VoxelStyleProfile>();
            root = EditorUtility.CreateGameObjectWithHideFlags("Transition fixture", HideFlags.HideAndDontSave, typeof(LODGroup), typeof(MeshRenderer));
            group = root.GetComponent<LODGroup>();
            renderer = root.GetComponent<MeshRenderer>();
            renderer.shadowCastingMode = ShadowCastingMode.Off;
            group.SetLODs(new[] { new LOD(.8f, new Renderer[] { renderer }), new LOD(.4f, new Renderer[] { renderer }), new LOD(.2f, new Renderer[] { renderer }) });
            group.size = 7;
            group.localReferencePoint = new Vector3(1, 2, 3);
            group.fadeMode = LODFadeMode.CrossFade;
            manifest = new VoxelLodSetManifest { productionMeshes = true, lods = new[] {
                new VoxelLodEntry { lodIndex = 0, sourceGuid = "unavailable-vox-0", builtSourceHash = "unchanged" },
                new VoxelLodEntry { lodIndex = 1, sourceGuid = "unavailable-vox-1" },
                new VoxelLodEntry { lodIndex = 2, sourceGuid = "unavailable-vox-2" } } };
        }

        [TearDown]
        public void TearDown()
        {
            if (root != null) Object.DestroyImmediate(root);
            if (profile != null && !EditorUtility.IsPersistent(profile)) Object.DestroyImmediate(profile);
        }

        [Test]
        public void Update_ChangesOnlyThresholdsAndManifestSize()
        {
            VoxelLodTransitionUpdater.ApplyToGroup(group, manifest, profile);
            var lods = group.GetLODs();
            for (int i = 0; i < lods.Length; i++)
            {
                Assert.That(lods[i].screenRelativeTransitionHeight, Is.EqualTo(profile.GetLodScreenHeight(i, 7)));
                Assert.That(manifest.lods[i].screenRelativeTransitionHeight, Is.EqualTo(lods[i].screenRelativeTransitionHeight));
                Assert.That(lods[i].renderers, Is.EqualTo(new[] { renderer }));
            }
            Assert.That(group.size, Is.EqualTo(7));
            Assert.That(manifest.lodGroupSize, Is.EqualTo(7));
            Assert.That(group.localReferencePoint, Is.EqualTo(new Vector3(1, 2, 3)));
            Assert.That(group.fadeMode, Is.EqualTo(LODFadeMode.CrossFade));
            Assert.That(renderer.shadowCastingMode, Is.EqualTo(ShadowCastingMode.Off));
            Assert.That(manifest.lods[0].builtSourceHash, Is.EqualTo("unchanged"));
        }

        [TestCase("count")]
        [TestCase("index")]
        [TestCase("impostor")]
        public void InvalidFamily_LeavesGroupAndManifestUntouched(string issue)
        {
            if (issue == "count") manifest.lods = new[] { manifest.lods[0] };
            if (issue == "index") manifest.lods[1].lodIndex = 3;
            if (issue == "impostor") manifest.impostor = new VoxelImpostorEntry { assetPath = "fake.asset" };
            Assert.That(() => VoxelLodTransitionUpdater.ApplyToGroup(group, manifest, profile), Throws.Exception);
            Assert.That(group.GetLODs()[0].screenRelativeTransitionHeight, Is.EqualTo(.8f));
            Assert.That(manifest.lods[0].screenRelativeTransitionHeight, Is.Zero);
        }

        [Test]
        public void SavedFamily_UpdatesWithoutReadingVoxOrRebuildingRenderers()
        {
            string folder = "Assets/VoxelBridgeTransitionTest_" + Guid.NewGuid().ToString("N");
            string backup = null;
            try
            {
                AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder));
                string profilePath = folder + "/Profile.asset", prefabPath = folder + "/Family.prefab", manifestPath = folder + "/Family.voxset.json";
                AssetDatabase.CreateAsset(profile, profilePath);
                root.hideFlags = HideFlags.None;
                var prefab = PrefabUtility.SaveAsPrefabAsset(root, prefabPath);
                root.hideFlags = HideFlags.HideAndDontSave;
                manifest.prefabAssetPath = prefabPath;
                manifest.prefabGuid = AssetDatabase.AssetPathToGUID(prefabPath);
                manifest.profileGuid = AssetDatabase.AssetPathToGUID(profilePath);
                VoxelLodPipeline.SaveManifest(manifestPath, manifest);
                // Only link identity is needed: VOX contents are deliberately unavailable.
                VoxelProductionLink.Store(prefab, profilePath, null, manifestPath);
                byte[] before = File.ReadAllBytes(VoxelLodPipeline.AssetPathToAbsolute(prefabPath));
                byte[] profileBefore = File.ReadAllBytes(VoxelLodPipeline.AssetPathToAbsolute(profilePath));
                backup = VoxelLodTransitionUpdater.Apply(manifestPath);
                Assert.That(File.ReadAllBytes(Path.Combine(backup, "Family.prefab")), Is.EqualTo(before));
                Assert.That(File.ReadAllBytes(VoxelLodPipeline.AssetPathToAbsolute(profilePath)), Is.EqualTo(profileBefore));
                Assert.That(AssetDatabase.AssetPathToGUID(prefabPath), Is.EqualTo(manifest.prefabGuid));
                var result = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
                Assert.That(result.GetComponent<LODGroup>().GetLODs()[0].screenRelativeTransitionHeight, Is.EqualTo(.6f));
                Assert.That(result.GetComponent<MeshRenderer>().shadowCastingMode, Is.EqualTo(ShadowCastingMode.Off));
                Assert.That(VoxelProductionFamily.Load(manifestPath).lods[0].screenRelativeTransitionHeight, Is.EqualTo(.6f));
                Assert.That(result.GetComponent<LODGroup>().size, Is.EqualTo(7));
                Assert.That(AssetDatabase.FindAssets("t:Mesh", new[] { folder }), Is.Empty);
            }
            finally
            {
                AssetDatabase.DeleteAsset(folder);
                if (backup != null) Directory.Delete(backup, true);
            }
        }
    }
}
