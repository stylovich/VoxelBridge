using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LocalModels.VoxelBridge.Tests
{
    public sealed class VoxelScaleNormalizationTests
    {
        [TestCase(1f)]
        [TestCase(2f)]
        [TestCase(10f)]
        public void SkinnedBake_AppliesRendererScaleOnce(float scale)
        {
            var root = new GameObject("Skinned scale test");
            Mesh mesh = null;
            try
            {
                var bone = new GameObject("Bone").transform; bone.SetParent(root.transform, false);
                var skin = new GameObject("Skin").AddComponent<SkinnedMeshRenderer>();
                skin.transform.SetParent(root.transform, false); skin.transform.localScale = Vector3.one * scale;
                var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
                mesh = Object.Instantiate(cube.GetComponent<MeshFilter>().sharedMesh); Object.DestroyImmediate(cube);
                mesh.vertices = mesh.vertices.Select(v => v * .2f).ToArray(); mesh.RecalculateBounds();
                mesh.bindposes = new[] { bone.worldToLocalMatrix * skin.transform.localToWorldMatrix };
                mesh.boneWeights = mesh.vertices.Select(v => new BoneWeight { boneIndex0 = 0, weight0 = 1 }).ToArray();
                skin.sharedMesh = mesh; skin.bones = new[] { bone }; skin.rootBone = bone;
                var b = MeshVoxelizer.GetSourceBounds(root, false);
                Assert.That(b.size.x, Is.EqualTo(.2f * scale).Within(.0001f));
                Assert.That(b.size.y, Is.EqualTo(.2f * scale).Within(.0001f));
            }
            finally { Object.DestroyImmediate(root); if (mesh) Object.DestroyImmediate(mesh); }
        }

        [TestCase(1f, VoxelSourceAxes.PreserveLocalAxes)]
        [TestCase(2f, VoxelSourceAxes.PreserveLocalAxes)]
        [TestCase(2f, VoxelSourceAxes.ZUp)]
        public void NormalizedGeometryAndPlacement_PreserveWorldPoints(float scale, VoxelSourceAxes axes)
        {
            var parent = new GameObject("Scaled parent"); var destination = new GameObject("Output");
            try
            {
                parent.transform.localScale = Vector3.one * 3;
                parent.transform.SetPositionAndRotation(new Vector3(3, 1, 7), Quaternion.Euler(0, 20, 0));
                var source = GameObject.CreatePrimitive(PrimitiveType.Cube);
                source.transform.SetParent(parent.transform, false); source.transform.localScale = new Vector3(scale, scale * 2, scale);
                source.transform.localPosition = new Vector3(1, 2, 3); source.transform.localRotation = Quaternion.Euler(15, 25, 5);
                var baked = VoxelScaleNormalization.SourceScale(source);
                destination.transform.SetParent(VoxelScaleNormalization.UnscaledParent(source.transform.parent), false);
                VoxelScaleNormalization.Place(source.transform, destination.transform, axes);
                VoxelSourceOrientationTests.AssertEquivalent(source.transform.localToWorldMatrix,
                    destination.transform.localToWorldMatrix * VoxelSourceOrientation.ToUnity(axes) * Matrix4x4.Scale(baked));
                Assert.That(destination.transform.localScale, Is.EqualTo(Vector3.one));
                Assert.That(Vector3.Distance(destination.transform.lossyScale, Vector3.one), Is.LessThan(.00001f));
                var b = MeshVoxelizer.GetSourceBounds(source, false, rootScale: baked);
                Assert.That(Vector3.Distance(b.size, baked), Is.LessThan(.0001));
            }
            finally { Object.DestroyImmediate(parent); Object.DestroyImmediate(destination); }
        }

        [Test]
        public void Normalize_RejectsZeroScaleAndShear()
        {
            var parent = new GameObject("Parent");
            try
            {
                var source = new GameObject("Source"); source.transform.SetParent(parent.transform, false);
                source.transform.localScale = new Vector3(0, 1, 1);
                Assert.Throws<InvalidOperationException>(() => VoxelScaleNormalization.SourceScale(source));
                source.transform.localScale = Vector3.one;
                parent.transform.localScale = new Vector3(2, 1, 1);
                source.transform.localRotation = Quaternion.Euler(0, 0, 45);
                Assert.Throws<InvalidOperationException>(() => VoxelScaleNormalization.SourceScale(source));
            }
            finally { Object.DestroyImmediate(parent); }
        }

        [Test]
        public void OldManifest_DoesNotNormalizePlacement()
        {
            Assert.That(JsonUtility.FromJson<VoxelLodSetManifest>("{}").normalizedScale, Is.False);
        }

        [TestCase(0)] [TestCase(1)] [TestCase(2)] [TestCase(3)]
        [TestCase(4)] [TestCase(5)] [TestCase(6)] [TestCase(7)]
        public void SignedHierarchy_NormalizationPreservesWorldMatrix(int signs)
        {
            var parent = new GameObject("Signed parent");
            var output = new GameObject("Normalized output");
            try
            {
                parent.transform.localScale = new Vector3((signs & 1) == 0 ? 2 : -2, (signs & 2) == 0 ? 2 : -2, (signs & 4) == 0 ? 2 : -2);
                parent.transform.SetPositionAndRotation(new Vector3(3, 4, -2), Quaternion.Euler(13, 21, 7));
                var source = new GameObject("Rotated child");
                source.transform.SetParent(parent.transform, false);
                source.transform.localRotation = Quaternion.Euler(31, 17, 43);
                source.transform.localScale = new Vector3(1, 2, 3);
                source.transform.localPosition = new Vector3(.3f, -.7f, 1.1f);
                Vector3 scale = VoxelScaleNormalization.SourceScale(source);
                foreach (var axes in new[] { VoxelSourceAxes.PreserveLocalAxes, VoxelSourceAxes.ZUp })
                {
                    VoxelScaleNormalization.Place(source.transform, output.transform, axes);
                    VoxelSourceOrientationTests.AssertEquivalent(source.transform.localToWorldMatrix,
                        output.transform.localToWorldMatrix * VoxelSourceOrientation.ToUnity(axes) * Matrix4x4.Scale(scale));
                    Assert.That(output.transform.localScale, Is.EqualTo(Vector3.one));
                    Assert.That(Vector3.Distance(output.transform.lossyScale, Vector3.one), Is.LessThan(.00001f));
                }
                Assert.That(scale.x < 0, Is.EqualTo(source.transform.localToWorldMatrix.determinant < 0));
            }
            finally { Object.DestroyImmediate(parent); Object.DestroyImmediate(output); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Batch_NormalizationSeparatesScaledSourcesAndInvalidatesEstimates(bool reflected)
        {
            string folder = "Assets/ScalePreviewTest_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder));
            var parent = new GameObject("Batch"); var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var profile = ScriptableObject.CreateInstance<VoxelStyleProfile>();
            try
            {
                var prefab = PrefabUtility.SaveAsPrefabAsset(cube, folder + "/Cube.prefab");
                for (int i = 0; i < 2; i++)
                {
                    var instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, parent.transform);
                    instance.transform.localScale = reflected ? new Vector3(i == 0 ? 1 : -1, 1, 1) : Vector3.one * (i + 1);
                }
                var safety = new VoxelLodBatchOptions { NormalizeScale = true, EnableCheckpoint = false };
                var options = new VoxelLodBuildOptions { NormalizeScale = true, GenerateLod0Only = true };
                var plans = VoxelLodPipeline.GetAutomaticBatchPlans(parent, safety);
                Assert.That(plans[0].ReuseKey, Is.Not.EqualTo(plans[1].ReuseKey));
                var estimates = VoxelLodBatchAnalyzer.Analyze(plans, profile, options, safety);
                Assert.That(estimates.Sources.Length, Is.EqualTo(2));
                string before = VoxelLodBatchAnalyzer.CreateSignature(plans, profile, options, safety);
                string checkpoint = VoxelLodBatchIdentity.CreateBatchSignature(plans, profile, options, safety);
                parent.transform.localScale = Vector3.one * 2;
                Assert.That(VoxelLodBatchAnalyzer.CreateSignature(plans, profile, options, safety), Is.Not.EqualTo(before));
                Assert.That(VoxelLodBatchIdentity.CreateBatchSignature(plans, profile, options, safety), Is.Not.EqualTo(checkpoint));
            }
            finally { Object.DestroyImmediate(parent); Object.DestroyImmediate(cube); Object.DestroyImmediate(profile); AssetDatabase.DeleteAsset(folder); }
        }

        [Test]
        public void NormalizedConversion_WritesPhysicalVoxAndPlacesUnitScale()
        {
            string folder = "Assets/ScaleConversionTest_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder));
            var parent = new GameObject("Parent"); var source = GameObject.CreatePrimitive(PrimitiveType.Cube);
            var profile = ScriptableObject.CreateInstance<VoxelStyleProfile>(); GameObject placed = null;
            try
            {
                AssetDatabase.CreateAsset(profile, folder + "/Profile.asset");
                parent.transform.localScale = Vector3.one * 2; source.transform.SetParent(parent.transform, false);
                source.transform.localScale = Vector3.one * .25f; source.transform.position = new Vector3(3.13f, 1, 2);
                var options = new VoxelLodBuildOptions { NormalizeScale = true, GenerateLod0Only = true,
                    ColorMode = VoxelColorMode.SingleColor, SingleColor = new Color32(100, 100, 100, 255), ExportFolder = folder };
                var result = VoxelLodPipeline.GenerateAutomatic(source, profile, options);
                Assert.That(VoxelLodPipeline.TryReadManifest(result.ManifestAssetPath, out var manifest), Is.True);
                Assert.That(manifest.normalizedScale, Is.True);
                Assert.That(manifest.bakedRootScale, Is.EqualTo(Vector3.one * .5f));
                VoxelLodBatchScenePlacement.PlaceSingle(source, result, profile, false);
                placed = source.scene.GetRootGameObjects().First(g => g.name.StartsWith(source.name + "_Voxel"));
                Assert.That(placed.transform.localScale, Is.EqualTo(Vector3.one));
                Assert.That(placed.transform.lossyScale, Is.EqualTo(Vector3.one));
                Assert.That(placed.transform.position, Is.EqualTo(source.transform.position));
                var bounds = placed.GetComponentInChildren<MeshRenderer>().bounds;
                Assert.That(bounds.size.x, Is.EqualTo(.5f).Within(profile.BaseVoxelSize * 2));
            }
            finally { if (placed) Object.DestroyImmediate(placed); Object.DestroyImmediate(source); Object.DestroyImmediate(parent); AssetDatabase.DeleteAsset(folder); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void NormalizedBatch_PreservesSizesUnderScaledParent(bool reflected)
        {
            string folder = "Assets/ScaleBatchTest_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", Path.GetFileName(folder));
            var parent = new GameObject("Scaled batch");
            var previousScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            var scene = UnityEditor.SceneManagement.EditorSceneManager.NewScene(
                UnityEditor.SceneManagement.NewSceneSetup.EmptyScene, UnityEditor.SceneManagement.NewSceneMode.Additive);
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(parent, scene);
            var profile = ScriptableObject.CreateInstance<VoxelStyleProfile>();
            GameObject output = null;
            try
            {
                AssetDatabase.CreateAsset(profile, folder + "/Profile.asset");
                parent.transform.localScale = new Vector3(reflected ? -2 : 2, 2, 2);
                parent.transform.position = new Vector3(1.13f, 2, 3);
                for (int i = 0; i < 2; i++)
                {
                    var source = GameObject.CreatePrimitive(PrimitiveType.Cube);
                    source.name = "ScaleCase" + i; source.transform.SetParent(parent.transform, false);
                    source.transform.localScale = Vector3.one * (.25f * (i + 1));
                    source.transform.localPosition = Vector3.right * i;
                }
                var options = new VoxelLodBuildOptions { NormalizeScale = true, GenerateLod0Only = true,
                    ColorMode = VoxelColorMode.SingleColor, SingleColor = new Color32(120, 120, 120, 255), ExportFolder = folder };
                var batch = VoxelLodPipeline.GenerateAutomaticBatch(parent, profile, options,
                    batchOptions: new VoxelLodBatchOptions { EnableCheckpoint = false });
                Assert.That(batch.FailedCount, Is.Zero);
                VoxelLodBatchScenePlacement.Place(parent, batch, profile, false);
                output = parent.scene.GetRootGameObjects().First(g => g.name == "Scaled batch_Voxel");
                Assert.That(output.transform.lossyScale, Is.EqualTo(Vector3.one));
                for (int i = 0; i < 2; i++)
                {
                    var child = output.transform.Find("ScaleCase" + i);
                    Assert.That(child.localScale, Is.EqualTo(Vector3.one));
                    Assert.That(child.position, Is.EqualTo(parent.transform.GetChild(i).position));
                    var b = child.GetComponentInChildren<MeshRenderer>().bounds;
                    Assert.That(b.size.x, Is.EqualTo(.5f * (i + 1)).Within(profile.BaseVoxelSize * 2));
                }
            }
            finally
            {
                if (output) Object.DestroyImmediate(output);
                Object.DestroyImmediate(parent);
                AssetDatabase.DeleteAsset(folder);
                UnityEngine.SceneManagement.SceneManager.SetActiveScene(previousScene);
                UnityEditor.SceneManagement.EditorSceneManager.CloseScene(scene, true);
            }
        }
    }
}
