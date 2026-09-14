using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace LocalModels.VoxelBridge.Tests
{
    public sealed class VoxelBlockSpomTests
    {
        internal const string ShaderPath = "Assets/VoxelBridge/Shaders/VoxelWorldBlockSpom.shadergraph";

        static Color[] Probe(Vector3 ray, int mode)
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/VoxelBridge/Editor/Tests/VoxelBlockSpomProbe.shader");
            Assert.That(shader && !ShaderUtil.ShaderHasError(shader), Is.True);
            var material = new Material(shader);
            var previous = RenderTexture.active;
            var rt = RenderTexture.GetTemporary(128, 128, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            Texture2D readable = null;
            try
            {
                material.SetVector("_TestRay", ray);
                material.SetFloat("_TestMode", mode);
                Graphics.Blit(Texture2D.whiteTexture, rt, material);
                RenderTexture.active = rt;
                readable = new Texture2D(128, 128, TextureFormat.RGBAFloat, false, true);
                readable.ReadPixels(new Rect(0, 0, 128, 128), 0, 0); readable.Apply();
                return readable.GetPixels();
            }
            finally
            {
                RenderTexture.active = previous;
                RenderTexture.ReleaseTemporary(rt);
                Object.DestroyImmediate(material);
                if (readable) Object.DestroyImmediate(readable);
            }
        }

        [TestCase(0, 0)]
        [TestCase(40, 15)]
        [TestCase(80, 5)]
        [TestCase(130, -25)]
        [TestCase(220, 45)]
        public void Volume_ClipsSilhouetteButNeverOpensHolesIntoSolidCore(float yaw, float pitch)
        {
            var ray = Quaternion.Euler(pitch, yaw, 0) * Vector3.forward;
            var volume = Probe(ray, 0);
            var proxy = Probe(ray, 1);
            var core = Probe(ray, 2);
            int clipped = 0, coreHits = 0;
            for (int i = 0; i < volume.Length; i++)
            {
                var v = volume[i];
                Assert.That(float.IsFinite(v.r) && float.IsFinite(v.g) && float.IsFinite(v.b) && float.IsFinite(v.a), Is.True);
                if (proxy[i].r >= 0 && v.r < 0) clipped++;
                if (core[i].r >= 0)
                {
                    coreHits++;
                    Assert.That(v.r, Is.InRange(0, core[i].r + .0001f), "The solid core must close every internal joint.");
                }
                if (v.r < 0) continue;
                Assert.That(proxy[i].r, Is.GreaterThanOrEqualTo(0), "The proxy must enclose the complete volume.");
                Assert.That(v.r, Is.GreaterThanOrEqualTo(proxy[i].r - .0001f));
                var normal = new Vector3(v.g, v.b, v.a);
                Assert.That(normal.magnitude, Is.EqualTo(1).Within(.0001f));
                Assert.That(Vector3.Dot(normal, ray), Is.LessThanOrEqualTo(.0001f));
            }
            Assert.That(coreHits, Is.GreaterThan(1000));
            Assert.That(clipped, Is.GreaterThan(5), "The silhouette must actually differ from the uncut proxy.");
        }

        [TestCase("Forward")]
        [TestCase("GBuffer")]
        [TestCase("DepthOnly")]
        [TestCase("MotionVectors")]
        [TestCase("ShadowCaster")]
        public void BlockShader_CompilesRasterAndDotsPasses(string passName)
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(ShaderPath);
            var sub = ShaderUtil.GetShaderData(shader).GetSubshader(0);
            var pass = Enumerable.Range(0, sub.PassCount).Select(sub.GetPass).First(p => p.Name == passName);
            var result = pass.CompileVariant(UnityEditor.Rendering.ShaderType.Fragment,
                new[] { "DOTS_INSTANCING_ON", "_DEPTHOFFSET_ON", "_CONSERVATIVE_DEPTH_OFFSET", "_ALPHATEST_ON",
                    "PUNCTUAL_SHADOW_MEDIUM", "DIRECTIONAL_SHADOW_MEDIUM", "AREA_SHADOW_MEDIUM" },
                UnityEditor.Rendering.ShaderCompilerPlatform.D3D, BuildTarget.StandaloneWindows64);
            Assert.That(result.Success, Is.True, string.Join("\n", result.Messages.Select(m => m.message)));
        }

        [TestCase(0)]
        [TestCase(40)]
        [TestCase(80)]
        public void Hdrp_RendersClosedBlockAndRecessedSilhouette(float orbit)
        {
            Texture2D plain = null, spom = null;
            try
            {
                plain = VoxelProductionGridRenderTests.Render(ShaderPath, true, gridMode: 0, profile: 1,
                    geometry: 3, orbit: orbit, pom: 1, pomMaxDepth: .01f, variation: .25f, surfaceId: 1);
                spom = VoxelProductionGridRenderTests.Render(ShaderPath, true, gridMode: 0, profile: 1,
                    geometry: 3, orbit: orbit, pom: 1, pomMaxDepth: .01f, variation: .25f, surfaceId: 1, depthWrite: true);
                var a = plain.GetPixels(); var b = spom.GetPixels();
                int removed = a.Zip(b, (x,y) => x.r > x.b * 1.25f && y.r <= y.b * 1.25f).Count(x=>x);
                Assert.That(removed, Is.GreaterThan(10), "SPOM must remove pixels at the proxy's silhouette.");
                Assert.That(b.Count(p=>p.r > p.b * 1.25f), Is.GreaterThan(10000), "The block must remain solid and visible.");
            }
            finally { if (plain) Object.DestroyImmediate(plain); if (spom) Object.DestroyImmediate(spom); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void Hdrp_ClosedVolumeKeepsExternalShadowsWithoutBlackCellAcne(bool directional)
        {
            Texture2D reference = null, self = null, blocked = null;
            try
            {
                reference = VoxelPomShadowTests.Render(true, false, 4096, directional: directional,
                    orbit: 40, variation: .25f, lightAngle: 55, blockSpom: true);
                self = VoxelPomShadowTests.Render(true, true, 4096, directional: directional,
                    orbit: 40, variation: .25f, lightAngle: 55, blockSpom: true);
                blocked = VoxelPomShadowTests.Render(true, true, 4096, blocker: true, directional: directional,
                    orbit: 40, variation: .25f, lightAngle: 55, blockSpom: true);
                Assert.That(reference.GetPixels().Zip(self.GetPixels(), (a,b)=>Mathf.Abs(a.r-b.r)).Average(), Is.LessThan(.01f));
                Assert.That(self.GetPixels().Zip(blocked.GetPixels(), (a,b)=>a.r-b.r>.05f).Count(x=>x), Is.GreaterThan(100));
            }
            finally
            {
                if (reference) Object.DestroyImmediate(reference);
                if (self) Object.DestroyImmediate(self);
                if (blocked) Object.DestroyImmediate(blocked);
            }
        }

        [Test]
        public void Hdrp_FamilyLocalVolumeSurvivesWorldTranslation()
        {
            Texture2D origin = null, translated = null;
            try
            {
                origin = VoxelProductionGridRenderTests.Render(ShaderPath, true, gridMode: 0, profile: 1,
                    geometry: 3, orbit: 40, pom: 1, pomMaxDepth: .01f, variation: .25f, surfaceId: 1, depthWrite: true);
                translated = VoxelProductionGridRenderTests.Render(ShaderPath, true, gridMode: 0, profile: 1,
                    geometry: 4, orbit: 40, pom: 1, pomMaxDepth: .01f, variation: .25f, surfaceId: 1, depthWrite: true);
                Assert.That(origin.GetPixels().Zip(translated.GetPixels(), (a,b)=>Vector4.Distance(a,b)).Average(), Is.LessThan(.005f));
            }
            finally { if (origin) Object.DestroyImmediate(origin); if (translated) Object.DestroyImmediate(translated); }
        }
    }
}
