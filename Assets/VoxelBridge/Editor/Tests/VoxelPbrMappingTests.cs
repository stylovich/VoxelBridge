using System;
using System.Collections.Generic;
using System.IO;
using NUnit.Framework;
using UnityEngine;

namespace LocalModels.VoxelBridge.Tests
{
    public sealed class VoxelPbrMappingTests
    {
        private Material material;
        private Texture2D texture;
        private VoxelSurfacePalette palette;
        private VoxelSurfaceMappingProfile profile;

        [SetUp] public void SetUp()
        {
            material = new Material(Shader.Find("HDRP/Lit"));
            palette = ScriptableObject.CreateInstance<VoxelSurfacePalette>();
            profile = ScriptableObject.CreateInstance<VoxelSurfaceMappingProfile>(); profile.surfacePalette = palette;
        }
        [TearDown] public void TearDown()
        {
            UnityEngine.Object.DestroyImmediate(material); UnityEngine.Object.DestroyImmediate(texture);
            UnityEngine.Object.DestroyImmediate(profile); UnityEngine.Object.DestroyImmediate(palette);
        }

        [Test] public void Constants_IgnoreDisabledMaskAndMatchWithoutMeshUvs()
        {
            material.SetFloat("_Metallic", 1); material.SetFloat("_Smoothness", .66f);
            Mask(); material.DisableKeyword("_MASKMAP");
            using var sampler = new VoxelPbrSampler(material);
            Assert.That(sampler.TrySample(Vector2.zero, false, out float m, out float s, out string reason), Is.True, reason);
            Assert.That(m, Is.EqualTo(1)); Assert.That(s, Is.EqualTo(.66f));
            var match = new VoxelSurfaceMatcher(profile).Match(m, s);
            Assert.That(match.SurfaceId, Is.EqualTo(6)); Assert.That(match.Decision, Is.EqualTo(VoxelSurfaceDecision.Automatic));
        }

        private void Mask()
        {
            texture = new Texture2D(2, 1, TextureFormat.RGBA32, false, true) { wrapMode = TextureWrapMode.Clamp, filterMode = FilterMode.Point };
            texture.SetPixels(new[] { new Color(0, 1, 0, 1), new Color(1, 0, 1, 0) }); texture.Apply();
            material.SetTexture("_MaskMap", texture); material.EnableKeyword("_MASKMAP");
        }

        [Test] public void Mask_UsesRaRemapsAndBaseUvTransformNotMaskTransform()
        {
            Mask();
            material.SetFloat("_Metallic", .99f); material.SetFloat("_Smoothness", .99f);
            material.SetFloat("_MetallicRemapMin", .2f); material.SetFloat("_MetallicRemapMax", .8f);
            material.SetFloat("_SmoothnessRemapMin", .1f); material.SetFloat("_SmoothnessRemapMax", .9f);
            material.SetTextureOffset("_MaskMap", new Vector2(.5f, 0));
            using (var sampler = new VoxelPbrSampler(material))
            {
                Assert.That(sampler.TrySample(new Vector2(.25f, .5f), true, out float m, out float s, out string reason), Is.True, reason);
                Assert.That(m, Is.EqualTo(.2f).Within(.005)); Assert.That(s, Is.EqualTo(.9f).Within(.005));
                Assert.That(sampler.TrySample(new Vector2(.75f, .5f), true, out m, out s, out _), Is.True);
                Assert.That(m, Is.EqualTo(.8f).Within(.005)); Assert.That(s, Is.EqualTo(.1f).Within(.005));
                Assert.That(sampler.TrySample(Vector2.zero, false, out _, out _, out _), Is.False);
            }
            material.SetTextureOffset("_BaseColorMap", new Vector2(.5f, 0));
            using var shifted = new VoxelPbrSampler(material);
            Assert.That(shifted.TrySample(new Vector2(.25f, .5f), true, out float moved, out _, out _), Is.True);
            Assert.That(moved, Is.EqualTo(.8f).Within(.005));
        }

        [TestCase(TextureWrapMode.Repeat, 1f)]
        [TestCase(TextureWrapMode.Clamp, 0f)]
        [TestCase(TextureWrapMode.Mirror, 0f)]
        [TestCase(TextureWrapMode.MirrorOnce, 0f)]
        public void Mask_RespectsWrapping(TextureWrapMode wrap, float expected)
        {
            Mask(); texture.wrapModeU = wrap;
            using var sampler = new VoxelPbrSampler(material);
            Assert.That(sampler.TrySample(new Vector2(-.25f, .5f), true, out float m, out _, out string reason), Is.True, reason);
            Assert.That(m, Is.EqualTo(expected).Within(.005));
        }

        [Test] public void Mask_BilinearRepeatSamplesAcrossTexelBoundary()
        {
            Mask(); texture.filterMode = FilterMode.Bilinear; texture.wrapModeU = TextureWrapMode.Repeat;
            using var sampler = new VoxelPbrSampler(material);
            Assert.That(sampler.TrySample(new Vector2(0, .5f), true, out float m, out float s, out string reason), Is.True, reason);
            Assert.That(m, Is.EqualTo(.5f).Within(.005)); Assert.That(s, Is.EqualTo(.5f).Within(.005));
        }

        [TestCase("_MAPPING_PLANAR")]
        [TestCase("_MAPPING_TRIPLANAR")]
        [TestCase("_DETAIL_MAP")]
        [TestCase("_MATERIAL_FEATURE_CLEAR_COAT")]
        [TestCase("_MATERIAL_FEATURE_ANISOTROPY")]
        [TestCase("_SURFACE_TYPE_TRANSPARENT")]
        public void UnsupportedKeyword_DoesNotGuessFromOutOfSyncProperties(string keyword)
        {
            Mask(); material.EnableKeyword(keyword);
            using var sampler = new VoxelPbrSampler(material);
            Assert.That(sampler.TrySample(Vector2.zero, true, out _, out _, out string reason), Is.False);
            Assert.That(reason, Is.Not.Empty);
        }

        [Test] public void Mask_RejectsHdrAndInvalidConstants()
        {
            texture = new Texture2D(1, 1, TextureFormat.RGBAFloat, false, true);
            material.SetTexture("_MaskMap", texture); material.EnableKeyword("_MASKMAP");
            using (var sampler = new VoxelPbrSampler(material)) Assert.That(sampler.TrySample(Vector2.zero, true, out _, out _, out _), Is.False);
            material.DisableKeyword("_MASKMAP"); material.SetFloat("_Metallic", float.NaN);
            using var invalid = new VoxelPbrSampler(material);
            Assert.That(invalid.TrySample(Vector2.zero, true, out _, out _, out _), Is.False);
        }

        [Test] public void Matcher_RejectsWeakAndAmbiguousCandidatesDeterministically()
        {
            profile.candidateSurfaceIds = new List<int> { 3, 4 };
            profile.maximumDistance = .01f;
            Assert.That(new VoxelSurfaceMatcher(profile).Match(1, 1).Decision, Is.EqualTo(VoxelSurfaceDecision.TooDistant));
            palette.MutableEntries[3] = new VoxelSurfaceDefinition(3, "A", VoxelSurfaceRenderClass.Opaque, 0, .4f, 0, 1);
            palette.MutableEntries[4] = new VoxelSurfaceDefinition(4, "B", VoxelSurfaceRenderClass.Opaque, 0, .4f, 0, .5f);
            profile.minimumSeparation = 0;
            var match = new VoxelSurfaceMatcher(profile).Match(0, .4f);
            Assert.That(match.Decision, Is.EqualTo(VoxelSurfaceDecision.Ambiguous)); Assert.That(match.SurfaceId, Is.EqualTo(-1));
            profile.candidateSurfaceIds.Reverse();
            Assert.That(new VoxelSurfaceMatcher(profile).Match(0, .4f).Decision, Is.EqualTo(match.Decision));
            profile.candidateSurfaceIds.Remove(4);
            Assert.That(new VoxelSurfaceMatcher(profile).Match(0, .4f).SurfaceId, Is.EqualTo(3));
        }

        [Test] public void Profile_RejectsInvalidCandidatesWeightsAndThresholds()
        {
            foreach (var candidates in new[] { new List<int>(), new List<int> { 3, 3 }, new List<int> { 250 }, new List<int> { 13 } })
            { profile.candidateSurfaceIds = candidates; Assert.Throws<InvalidDataException>(() => profile.GetCandidates()); }
            profile.candidateSurfaceIds = new List<int> { 3 };
            profile.metallicWeight = profile.smoothnessWeight = 0; Assert.Throws<InvalidDataException>(() => profile.GetCandidates());
            profile.metallicWeight = float.NaN; Assert.Throws<InvalidDataException>(() => profile.GetCandidates());
            profile.metallicWeight = 1; profile.maximumDistance = 2; Assert.Throws<InvalidDataException>(() => profile.GetCandidates());
            profile.maximumDistance = .18f; profile.minimumSeparation = -1; Assert.Throws<InvalidDataException>(() => profile.GetCandidates());
        }
    }
}
