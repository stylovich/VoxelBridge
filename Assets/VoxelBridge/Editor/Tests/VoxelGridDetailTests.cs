using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace LocalModels.VoxelBridge.Tests
{
    public class VoxelGridDetailTests
    {
        private static Color[] Render(float enabled = 1, float distance = 1, float offset = 0, float span = .25f)
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
