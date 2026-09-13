using System;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace LocalModels.VoxelBridge.Tests
{
    public sealed class VoxelSurfaceHeightTests
    {
        [Test]
        public void VariationRowPreservesPbrAndLeavesGlassFlat()
        {
            var palette = ScriptableObject.CreateInstance<VoxelSurfacePalette>();
            try
            {
                Assert.That(VoxelPaletteLutBuilder.TryBuildSurfacePixels(palette, out var original, out var oldHash, out _), Is.True);
                Assert.That(original.Length, Is.EqualTo(256));
                var source = palette.MutableEntries[1];
                palette.MutableEntries[1] = new VoxelSurfaceDefinition(1, source.DisplayName, source.RenderClass,
                    source.Metallic, source.Smoothness, source.Emission, source.OcclusionMultiplier, source.Opacity, .04f);
                var glass = palette.MutableEntries.Single(s => s.RenderClass == VoxelSurfaceRenderClass.Transparent);
                palette.MutableEntries[palette.MutableEntries.IndexOf(glass)] = new VoxelSurfaceDefinition(glass.Id, glass.DisplayName,
                    glass.RenderClass, glass.Metallic, glass.Smoothness, glass.Emission, glass.OcclusionMultiplier, glass.Opacity, .2f);
                Assert.That(VoxelPaletteLutBuilder.TryBuildSurfacePixels(palette, out var varied, out var newHash, out _), Is.True);
                Assert.That(varied.Length, Is.EqualTo(512));
                CollectionAssert.AreEqual(original, varied.Take(256).ToArray());
                Assert.That(varied[257].r / 255f * .25f, Is.EqualTo(.04f).Within(.0005f));
                Assert.That(varied[256 + glass.Id].r, Is.Zero);
                Assert.That(varied[256 + 15].r, Is.Zero, "Polished metal remains opt-in, not inferred from roughness.");
                Assert.That(newHash, Is.Not.EqualTo(oldHash));
                palette.MutableEntries[1] = source;
                Assert.That(VoxelPaletteLutBuilder.TryBuildSurfacePixels(palette, out var restored, out var restoredHash, out _), Is.True);
                CollectionAssert.AreEqual(original, restored); Assert.That(restoredHash, Is.EqualTo(oldHash));
            }
            finally { UnityEngine.Object.DestroyImmediate(palette); }
        }

        [TestCase(-.01f)]
        [TestCase(.251f)]
        [TestCase(float.NaN)]
        [TestCase(float.PositiveInfinity)]
        public void VariationValidationRejectsInvalidValues(float variation)
        {
            var palette = ScriptableObject.CreateInstance<VoxelSurfacePalette>();
            try
            {
                palette.MutableEntries[0] = new VoxelSurfaceDefinition(0, "Default", VoxelSurfaceRenderClass.Opaque,
                    0, 0, 0, 1, cellHeightVariation: variation);
                Assert.That(palette.TryValidate(out var error), Is.False);
                Assert.That(error, Does.Contain("Cell Height Variation"));
            }
            finally { UnityEngine.Object.DestroyImmediate(palette); }
        }

        [Test]
        public void VariationLutResizePreservesGuidAndIsReversible()
        {
            string folder = "Assets/VoxelHeightTest_" + Guid.NewGuid().ToString("N");
            AssetDatabase.CreateFolder("Assets", folder.Substring(7));
            var palette = ScriptableObject.CreateInstance<VoxelSurfacePalette>();
            AssetDatabase.CreateAsset(palette, folder + "/Surface.asset");
            try
            {
                Assert.That(VoxelPaletteLutGenerator.TryRebuild(palette, out var lut, out var error), Is.True, error);
                string path = AssetDatabase.GetAssetPath(lut), guid = AssetDatabase.AssetPathToGUID(path);
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(lut, out string originalGuid, out long originalFileId);
                var material = new Material(AssetDatabase.LoadAssetAtPath<Shader>(VoxelProductionExporter.ShaderPath));
                material.SetTexture("_PaletteSurface", lut);
                AssetDatabase.CreateAsset(material, folder + "/Material.mat");
                var serialized = new SerializedObject(palette);
                var value = serialized.FindProperty("entries").GetArrayElementAtIndex(1).FindPropertyRelative("cellHeightVariation");
                value.floatValue = .04f; serialized.ApplyModifiedProperties();
                Assert.That(VoxelPaletteLutGenerator.IsCurrent(palette), Is.False);
                Assert.That(VoxelPaletteLutGenerator.TryRebuild(palette, out var grown, out error), Is.True, error);
                Assert.That(grown.height, Is.EqualTo(2));
                Assert.That(AssetDatabase.AssetPathToGUID(path), Is.EqualTo(guid));
                AssetDatabase.TryGetGUIDAndLocalFileIdentifier(grown, out string grownGuid, out long grownFileId);
                Assert.That(grownGuid, Is.EqualTo(originalGuid)); Assert.That(grownFileId, Is.EqualTo(originalFileId));
                Assert.That(material.GetTexture("_PaletteSurface") == grown, Is.True, "Material reference must survive texture resizing.");
                Assert.That(VoxelPaletteLutGenerator.IsCurrent(palette), Is.True);
                serialized.Update(); value.floatValue = 0; serialized.ApplyModifiedProperties();
                Assert.That(VoxelPaletteLutGenerator.TryRebuild(palette, out var shrunk, out error), Is.True, error);
                Assert.That(shrunk.height, Is.EqualTo(1)); Assert.That(AssetDatabase.AssetPathToGUID(path), Is.EqualTo(guid));
                Assert.That(VoxelPaletteLutGenerator.IsCurrent(palette), Is.True);
            }
            finally { AssetDatabase.DeleteAsset(folder); }
        }
    }
}
