using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LocalModels.VoxelBridge.Tests
{
    public sealed class VoxelFamilySpomTests
    {
        internal static Texture3D Volume(VoxelGrid grid, VoxelSurfacePalette palette)
        {
            var texture = new Texture3D(grid.Size.x, grid.Size.y, grid.Size.z,
                UnityEngine.Experimental.Rendering.GraphicsFormat.R8G8B8A8_UNorm,
                UnityEngine.Experimental.Rendering.TextureCreationFlags.None)
                { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
            texture.SetPixels32(VoxelFamilySpomData.BuildPixels(grid, palette)); texture.Apply();
            return texture;
        }

        static Color Probe(VoxelGrid grid, Vector3 origin, Vector3 ray, float depth = .01f)
        {
            var palette = ScriptableObject.CreateInstance<VoxelSurfacePalette>();
            Texture3D volume = null; Texture2D surfaces = null, pixels = null;
            Material material = null; RenderTexture rt = null;
            var previous = RenderTexture.active;
            try
            {
                volume = Volume(grid, palette);
                surfaces = new Texture2D(256, 2, TextureFormat.RGBA32, false, true) { filterMode = FilterMode.Point };
                surfaces.SetPixels(Enumerable.Repeat(new Color(0, .3f, 0, 1), 256)
                    .Concat(Enumerable.Repeat(new Color(1, 0, 0, 0), 256)).ToArray()); surfaces.Apply();
                var shader = AssetDatabase.LoadAssetAtPath<Shader>("Assets/VoxelBridge/Editor/Tests/VoxelFamilySpomProbe.shader");
                Assert.That(shader && !ShaderUtil.ShaderHasError(shader), Is.True);
                material = new Material(shader);
                material.SetTexture("_Volume", volume); material.SetTexture("_Surfaces", surfaces);
                material.SetVector("_Size", (Vector3)grid.Size); material.SetVector("_Origin", origin); material.SetVector("_Ray", ray);
                material.SetFloat("_Depth", depth);
                rt = RenderTexture.GetTemporary(4, 4, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
                Graphics.Blit(Texture2D.whiteTexture, rt, material); RenderTexture.active = rt;
                pixels = new Texture2D(4, 4, TextureFormat.RGBAFloat, false, true);
                pixels.ReadPixels(new Rect(0, 0, 4, 4), 0, 0); pixels.Apply();
                return pixels.GetPixel(1, 1);
            }
            finally
            {
                RenderTexture.active = previous; if (rt) RenderTexture.ReleaseTemporary(rt);
                if (volume) Object.DestroyImmediate(volume); if (surfaces) Object.DestroyImmediate(surfaces);
                if (pixels) Object.DestroyImmediate(pixels); if (material) Object.DestroyImmediate(material);
                Object.DestroyImmediate(palette);
            }
        }

        [Test]
        public void Data_KeepsIdsAndDoesNotExposeChunkBoundaries()
        {
            var palette = ScriptableObject.CreateInstance<VoxelSurfacePalette>();
            try
            {
                var grid = new VoxelGrid(new Vector3Int(130, 3, 3), Vector3.zero, .0625f, true);
                Array.Fill(grid.Occupied, true);
                grid.SemanticIds[grid.Index(128, 1, 1)] = VoxelSemanticEncoding.Pack(241, 6);
                var pixels = VoxelFamilySpomData.BuildPixels(grid, palette);
                Assert.That(pixels[grid.Index(127, 1, 1)].b & 2, Is.Zero);
                Assert.That(pixels[grid.Index(128, 1, 1)].b & 1, Is.Zero);
                Assert.That(pixels[grid.Index(128, 1, 1)], Is.EqualTo(new Color32(241, 6, 0, 255)));
                grid.SemanticIds[grid.Index(128, 1, 1)] = VoxelSemanticEncoding.Pack(0, 44);
                pixels = VoxelFamilySpomData.BuildPixels(grid, palette);
                Assert.That(pixels[grid.Index(128, 1, 1)].a, Is.Zero, "Glass stays outside the opaque volume.");
                Assert.That(pixels[grid.Index(127, 1, 1)].b & 2, Is.EqualTo(2));
                Assert.That(pixels[0].a, Is.EqualTo(255), "ColorID zero is not empty.");
            }
            finally { Object.DestroyImmediate(palette); }
        }

        [Test]
        public void Data_RejectsUnsupportedVolumeAndUnknownSurface()
        {
            var palette = ScriptableObject.CreateInstance<VoxelSurfacePalette>();
            try
            {
                Assert.Throws<InvalidDataException>(() => VoxelFamilySpomData.BuildPixels(new VoxelGrid(new Vector3Int(257, 1, 1), Vector3.zero, .0625f, true), palette));
                var grid = new VoxelGrid(Vector3Int.one, Vector3.zero, .0625f, true);
                grid.Occupied[0] = true; grid.SemanticIds[0] = VoxelSemanticEncoding.Pack(0, 255);
                Assert.Throws<InvalidDataException>(() => VoxelFamilySpomData.BuildPixels(grid, palette));
            }
            finally { Object.DestroyImmediate(palette); }
        }

        [TestCase(0)]
        [TestCase(241)]
        public void Trace_UsesOpaqueOccupancyAndHitAttributes(int color)
        {
            var grid = new VoxelGrid(new Vector3Int(4, 4, 4), Vector3.zero, .0625f, true);
            int i = grid.Index(1, 1, 2); grid.Occupied[i] = true; grid.SemanticIds[i] = VoxelSemanticEncoding.Pack(color, 6);
            var hit = Probe(grid, new Vector3(.09375f, .09375f, -.5f), Vector3.forward);
            Assert.That(hit.r, Is.InRange(.625f, .636f));
            Assert.That(hit.g, Is.EqualTo(color)); Assert.That(hit.b, Is.EqualTo(6)); Assert.That(hit.a, Is.EqualTo(-1).Within(.001f));
            var miss = Probe(grid, new Vector3(.21875f, .21875f, -.5f), Vector3.forward);
            Assert.That(miss.r, Is.EqualTo(-1), "Empty space must not become an enclosing solid box.");
        }

        [Test]
        public void Trace_ReachesPastChunkBoundaryWithoutLosingData()
        {
            var grid = new VoxelGrid(new Vector3Int(130, 3, 3), Vector3.zero, .0625f, true);
            int i = grid.Index(129, 1, 1); grid.Occupied[i] = true; grid.SemanticIds[i] = VoxelSemanticEncoding.Pack(199, 0);
            var hit = Probe(grid, new Vector3(-.5f, .09375f, .09375f), Vector3.right);
            Assert.That(hit.r, Is.InRange(8.5625f, 8.573f)); Assert.That(hit.g, Is.EqualTo(199));
        }

        [Test]
        public void Frame_RejectsScaleReflectionAndShear()
        {
            VoxelFamilySpomLab.ValidateFrame(Matrix4x4.TRS(new Vector3(50, 0, -80), Quaternion.Euler(15, 55, 0), Vector3.one));
            Assert.Throws<InvalidDataException>(() => VoxelFamilySpomLab.ValidateFrame(Matrix4x4.Scale(Vector3.one * 2)));
            Assert.Throws<InvalidDataException>(() => VoxelFamilySpomLab.ValidateFrame(Matrix4x4.Scale(new Vector3(-1, 1, 1))));
            var shear = Matrix4x4.identity; shear.m01 = .2f;
            Assert.Throws<InvalidDataException>(() => VoxelFamilySpomLab.ValidateFrame(shear));
        }

        [TestCase("Forward")]
        [TestCase("GBuffer")]
        [TestCase("DepthOnly")]
        [TestCase("MotionVectors")]
        [TestCase("ShadowCaster")]
        public void Shader_CompilesRasterPasses(string name)
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(VoxelFamilySpomLab.ShaderPath);
            var sub = ShaderUtil.GetShaderData(shader).GetSubshader(0);
            var pass = Enumerable.Range(0, sub.PassCount).Select(sub.GetPass).First(p=>p.Name == name);
            var result = pass.CompileVariant(UnityEditor.Rendering.ShaderType.Fragment,
                new[] { "_DEPTHOFFSET_ON", "_CONSERVATIVE_DEPTH_OFFSET", "_ALPHATEST_ON", "DOTS_INSTANCING_ON",
                    "PUNCTUAL_SHADOW_MEDIUM", "DIRECTIONAL_SHADOW_MEDIUM", "AREA_SHADOW_MEDIUM" },
                UnityEditor.Rendering.ShaderCompilerPlatform.D3D, BuildTarget.StandaloneWindows64);
            Assert.That(result.Success, Is.True, string.Join("\n",result.Messages.Select(m=>m.message)));
        }
    }
}
