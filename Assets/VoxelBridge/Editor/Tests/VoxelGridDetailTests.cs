using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace LocalModels.VoxelBridge.Tests
{
    public class VoxelGridDetailTests
    {
        private static Color[] Render(float enabled = 1, float distance = 1, float offset = 0, float span = .25f,
            float multiscale = 0, float targetPixels = 12, float maxLevels = 4)
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/VoxelBridge/Editor/Tests/VoxelGridDetailProbe.shader");
            Assert.That(shader, Is.Not.Null);
            Assert.That(ShaderUtil.ShaderHasError(shader), Is.False);
            var material = new Material(shader);
            var rt = RenderTexture.GetTemporary(64, 64, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            var previous = RenderTexture.active;
            var camera = Shader.GetGlobalVector("_WorldSpaceCameraPos");
            Texture2D readable = null;
            try
            {
                Shader.SetGlobalVector("_WorldSpaceCameraPos", Vector4.zero);
                material.SetFloat("_TestEnabled", enabled); material.SetFloat("_TestDistance", distance);
                material.SetFloat("_TestOffset", offset); material.SetFloat("_TestSpan", span);
                material.SetFloat("_TestMultiscale", multiscale); material.SetFloat("_TestTargetPixels", targetPixels);
                material.SetFloat("_TestMaxScaleLevels", maxLevels);
                Graphics.Blit(Texture2D.whiteTexture, rt, material);
                RenderTexture.active = rt;
                readable = new Texture2D(64, 64, TextureFormat.RGBAFloat, false, true);
                readable.ReadPixels(new Rect(0, 0, 64, 64), 0, 0); readable.Apply();
                return readable.GetPixels();
            }
            finally
            {
                Shader.SetGlobalVector("_WorldSpaceCameraPos", camera);
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
                Object.DestroyImmediate(material);
                if (readable != null) Object.DestroyImmediate(readable);
            }
        }

        [TestCase(0, 1, .25f)]
        [TestCase(1, 9, .25f)]
        [TestCase(1, 1, 2f)]
        public void DisabledDistantOrUnresolvedGrid_PreservesBase(float enabled, float distance, float span)
        {
            foreach (var c in Render(enabled, distance, 0, span))
            {
                Assert.That(c.r, Is.EqualTo(.5f).Within(.001f));
                Assert.That(c.g, Is.EqualTo(.5f).Within(.001f));
                Assert.That(c.b, Is.EqualTo(1).Within(.001f));
                Assert.That(c.a, Is.EqualTo(.6f).Within(.001f));
            }
        }

        [Test]
        public void CloseGrid_ChangesNormalAndRoughness()
        {
            var pixels = Render();
            Assert.That(pixels.Any(c => Mathf.Abs(c.r - .5f) > .01f), Is.True);
            Assert.That(pixels.Any(c => c.a < .59f), Is.True);
            Assert.That(pixels.All(c => float.IsFinite(c.r) && float.IsFinite(c.g) && float.IsFinite(c.b) && float.IsFinite(c.a)), Is.True);
        }

        [Test]
        public void WholeCellOffset_PreservesGridPhase()
        {
            var a = Render(); var b = Render(offset: .03125f);
            for (int i = 0; i < a.Length; i++)
                Assert.That(Vector4.Distance(a[i], b[i]), Is.LessThan(.001f));
        }

        [Test]
        public void Multiscale_KeepsDistantGridWhereFixedIsInvisible()
        {
            Assert.That(Render(distance: 30, span: 2).All(c => Mathf.Abs(c.a - .6f) < .001f), Is.True);
            var pixels = Render(distance: 30, span: 2, multiscale: 1);
            Assert.That(pixels.Any(c => c.a < .59f), Is.True);
            Assert.That(pixels.Any(c => Mathf.Abs(c.r - .5f) > .01f), Is.True);
        }

        [Test]
        public void Multiscale_ApproachesFixedWhenFineGridIsResolved()
        {
            var a = Render(span: .125f);
            var b = Render(span: .125f, multiscale: 1);
            for (int i = 0; i < a.Length; i++) Assert.That(Vector4.Distance(a[i], b[i]), Is.LessThan(.001f));
        }

        [TestCase(1f)]
        [TestCase(2f)]
        [TestCase(4f)]
        public void Multiscale_HasContinuousBinaryTransitions(float octave)
        {
            // Base footprint is .125 cells/pixel. Changing target size crosses an exact octave
            // without changing the sampled positions, isolating continuity of the blend.
            var a = Render(multiscale: 1, targetPixels: 8 * octave * .9999f);
            var b = Render(multiscale: 1, targetPixels: 8 * octave * 1.0001f);
            for (int i = 0; i < a.Length; i++) Assert.That(Vector4.Distance(a[i], b[i]), Is.LessThan(.002f));
        }

        [Test]
        public void Multiscale_CoarsestCellTranslationPreservesPhase()
        {
            var a = Render(span: 2, multiscale: 1);
            var b = Render(span: 2, multiscale: 1, offset: .5f);
            for (int i = 0; i < a.Length; i++) Assert.That(Vector4.Distance(a[i], b[i]), Is.LessThan(.001f));
        }

        [TestCase(0, 2, 4)]
        [TestCase(1, 64, 4)]
        [TestCase(1, 2, 0)]
        public void Multiscale_DisabledOrBeyondCapRemainsFlat(float enabled, float span, float maxLevels)
        {
            foreach (var c in Render(enabled: enabled, span: span, multiscale: 1, maxLevels: maxLevels))
                Assert.That(Vector4.Distance(c, new Color(.5f, .5f, 1, .6f)), Is.LessThan(.001f));
        }

        [TestCase("Forward", false)]
        [TestCase("Forward", true)]
        [TestCase("GBuffer", false)]
        [TestCase("GBuffer", true)]
        public void Prototype_CompilesRasterVariants(string passName, bool dots)
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/VoxelBridge/Shaders/VoxelGridPrototype.shadergraph");
            Assert.That(shader, Is.Not.Null);
            var sub = ShaderUtil.GetShaderData(shader).GetSubshader(0);
            var pass = Enumerable.Range(0, sub.PassCount).Select(sub.GetPass).First(p => p.Name == passName);
            var keywords = new[] { "PUNCTUAL_SHADOW_MEDIUM", "DIRECTIONAL_SHADOW_MEDIUM", "AREA_SHADOW_MEDIUM" };
            if (dots) keywords = keywords.Concat(new[] { "DOTS_INSTANCING_ON" }).ToArray();
            var result = pass.CompileVariant(UnityEditor.Rendering.ShaderType.Fragment, keywords,
                UnityEditor.Rendering.ShaderCompilerPlatform.D3D, BuildTarget.StandaloneWindows64);
            Assert.That(result.Success, Is.True, string.Join("\n", result.Messages.Select(m => m.message)));
        }
    }
}
