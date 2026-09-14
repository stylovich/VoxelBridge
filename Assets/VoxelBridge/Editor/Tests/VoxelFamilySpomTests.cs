using System;
using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
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
            var pass = Enumerable.Range(0, sub.PassCount).Select(sub.GetPass).First(p => p.Name == name);
            var result = pass.CompileVariant(UnityEditor.Rendering.ShaderType.Fragment,
                new[] { "_DEPTHOFFSET_ON", "_CONSERVATIVE_DEPTH_OFFSET", "_ALPHATEST_ON", "DOTS_INSTANCING_ON",
                    "PUNCTUAL_SHADOW_MEDIUM", "DIRECTIONAL_SHADOW_MEDIUM", "AREA_SHADOW_MEDIUM" },
                UnityEditor.Rendering.ShaderCompilerPlatform.D3D, BuildTarget.StandaloneWindows64);
            Assert.That(result.Success, Is.True, string.Join("\n", result.Messages.Select(m => m.message)));
        }

        internal static Texture2D RenderCorner(int chunkSize, bool enabled, bool castShadows = false)
        {
            var grid = new VoxelGrid(new Vector3Int(32, 8, 8), new Vector3(-1, -.25f, -.25f), .0625f, true);
            for (int z = 0; z < 8; z++)
                for (int y = 0; y < 8; y++)
                    for (int x = 0; x < 32; x++)
                    {
                        bool hole = (x == 15 || x == 16) && (y == 3 || y == 4);
                        int i = grid.Index(x, y, z);
                        grid.Occupied[i] = (z < 2 && !hole) || x < 2;
                        grid.SemanticIds[i] = VoxelSemanticEncoding.Pack(x < 16 ? 17 : 200, x < 16 ? 0 : 6);
                    }
            var palette = ScriptableObject.CreateInstance<VoxelSurfacePalette>();
            var preview = new PreviewRenderUtility();
            Texture3D volume = null; Texture2D colors = null, surfaces = null, result = null;
            Material material = null; Mesh[] meshes = null; RenderTexture rt = null;
            VolumeProfile profile = null;
            var previous = RenderTexture.active;
            try
            {
                volume = Volume(grid, palette);
                colors = new Texture2D(256, 1, TextureFormat.RGBA32, false, true) { filterMode = FilterMode.Point };
                colors.SetPixels(Enumerable.Repeat(Color.gray, 256).ToArray());
                colors.SetPixel(17, 0, new Color(.7f, .2f, .1f, 1)); colors.SetPixel(200, 0, new Color(.1f, .2f, .7f, 1)); colors.Apply();
                surfaces = new Texture2D(256, 2, TextureFormat.RGBA32, false, true) { filterMode = FilterMode.Point };
                surfaces.SetPixels(Enumerable.Repeat(new Color(0, .3f, 0, 1), 256).Concat(Enumerable.Repeat(Color.clear, 256)).ToArray());
                surfaces.SetPixel(0, 1, Color.red); surfaces.Apply();
                material = new Material(AssetDatabase.LoadAssetAtPath<Shader>(VoxelFamilySpomLab.ShaderPath));
                material.SetTexture("_SpomVolume", volume); material.SetVector("_SpomOrigin", grid.Origin);
                material.SetVector("_SpomSize", (Vector3)grid.Size); material.SetFloat("_SpomVoxelSize", grid.VoxelSize);
                material.SetTexture("_PaletteColor", colors); material.SetTexture("_PaletteSurface", surfaces);
                material.SetFloat("_GridEnabled", enabled ? 1 : 0); material.SetFloat("_GridPomEnabled", 1);
                material.SetFloat("_GridJointWidth", .04f); material.SetFloat("_GridBevelWidth", .04f);
                material.SetFloat("_GridJointDepth", .025f); material.SetFloat("_GridPomMaxDepth", .01f);
                material.SetFloat("_DepthOffsetEnable", 1); material.SetFloat("_ConservativeDepthOffsetEnable", 1);
                HDMaterial.ValidateMaterial(material);
                meshes = VoxelSemanticMesher.BuildChunks(grid, chunkSize, false, palette: palette);
                foreach (var mesh in meshes)
                {
                    var go = new GameObject("Family test chunk"); preview.AddSingleGO(go);
                    go.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var renderer = go.AddComponent<MeshRenderer>(); renderer.sharedMaterial = material;
                    renderer.lightProbeUsage = LightProbeUsage.Off; renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
                    renderer.rayTracingMode = UnityEngine.Experimental.Rendering.RayTracingMode.Off;
                    renderer.shadowCastingMode = castShadows ? ShadowCastingMode.On : ShadowCastingMode.Off;
                }
                var camera = preview.camera; camera.cameraType = CameraType.Game; camera.fieldOfView = 50;
                camera.nearClipPlane = .1f; camera.farClipPlane = 5;
                camera.transform.SetPositionAndRotation(new Vector3(0, .05f, -1.2f), Quaternion.LookRotation(new Vector3(0, -.05f, 1.1f)));
                var hdCamera = camera.gameObject.AddComponent<HDAdditionalCameraData>();
                hdCamera.clearColorMode = HDAdditionalCameraData.ClearColorMode.Color; hdCamera.backgroundColorHDR = Color.black;
                hdCamera.volumeLayerMask = 1 << 31; hdCamera.customRenderingSettings = true;
                foreach (var field in new[] { FrameSettingsField.RayTracing, FrameSettingsField.Postprocess, FrameSettingsField.CustomPass,
                    FrameSettingsField.ContactShadows, FrameSettingsField.AtmosphericScattering, FrameSettingsField.SSAO,
                    FrameSettingsField.SSR, FrameSettingsField.ReflectionProbe, FrameSettingsField.AdaptiveProbeVolume })
                { hdCamera.renderingPathCustomFrameSettingsOverrideMask.mask[(uint)field] = true; hdCamera.renderingPathCustomFrameSettings.SetEnabled(field, false); }
                var volumeGo = new GameObject("Family test volume") { layer = 31 }; preview.AddSingleGO(volumeGo);
                var testVolume = volumeGo.AddComponent<UnityEngine.Rendering.Volume>(); testVolume.isGlobal = true; testVolume.priority = 10000;
                profile = ScriptableObject.CreateInstance<VolumeProfile>(); testVolume.sharedProfile = profile;
                var indirect = profile.Add<IndirectLightingController>(); indirect.indirectDiffuseLightingMultiplier.Override(0); indirect.reflectionLightingMultiplier.Override(0);
                var exposure = profile.Add<Exposure>(); exposure.mode.Override(ExposureMode.Fixed); exposure.fixedExposure.Override(0);
                var shadowSettings = profile.Add<HDShadowSettings>(); shadowSettings.maxShadowDistance.Override(4); shadowSettings.cascadeShadowSplitCount.Override(1);
                preview.lights[1].gameObject.SetActive(false);
                var light = preview.lights[0]; var hdLight = light.gameObject.AddComponent<HDAdditionalLightData>();
                light.type = LightType.Directional; light.lightUnit = LightUnit.Lux; light.intensity = 4;
                light.useColorTemperature = false; light.color = Color.white; light.shadows = LightShadows.Soft;
                light.transform.rotation = Quaternion.Euler(25, 35, 0); hdLight.SetShadowResolution(4096);
                Texture rendered = null;
                for (int frame = 0; frame < 3; frame++)
                {
                    preview.BeginPreview(new Rect(0, 0, 256, 256), GUIStyle.none);
                    try { preview.Render(true, false); } finally { rendered = preview.EndPreview(); }
                }
                rt = RenderTexture.GetTemporary(256, 256, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(rendered, rt); RenderTexture.active = rt;
                result = new Texture2D(256, 256, TextureFormat.RGBA32, false);
                result.ReadPixels(new Rect(0, 0, 256, 256), 0, 0); result.Apply(); return result;
            }
            catch { if (result) Object.DestroyImmediate(result); throw; }
            finally
            {
                RenderTexture.active = previous; if (rt) RenderTexture.ReleaseTemporary(rt);
                preview.Cleanup(); if (volume) Object.DestroyImmediate(volume);
                if (colors) Object.DestroyImmediate(colors); if (surfaces) Object.DestroyImmediate(surfaces);
                if (material) Object.DestroyImmediate(material); if (meshes != null) foreach (var mesh in meshes) Object.DestroyImmediate(mesh);
                if (profile) { foreach (var component in profile.components) Object.DestroyImmediate(component); Object.DestroyImmediate(profile); }
                Object.DestroyImmediate(palette);
            }
        }

        [Test]
        public void Hdrp_MatchesAcrossChunkSplitAndPreservesColorAndHole()
        {
            Texture2D whole = null, split = null, flat = null, shadow = null;
            try
            {
                whole = RenderCorner(32, true); split = RenderCorner(16, true); flat = RenderCorner(16, false); shadow = RenderCorner(16, true, true);
                var a = whole.GetPixels(); var b = split.GetPixels();
                Assert.That(a.Zip(b, (x, y) => Vector4.Distance(x, y)).Average(), Is.LessThan(.001f), "Chunk boundaries must not change the volume or IDs.");
                Assert.That(b.Count(p => p.r > p.b * 1.5f && p.r > .1f), Is.GreaterThan(1000));
                Assert.That(b.Count(p => p.b > p.r * 1.5f && p.b > .1f), Is.GreaterThan(1000));
                Assert.That(a.Zip(flat.GetPixels(), (x, y) => Vector4.Distance(x, y)).Count(d => d > .01f), Is.GreaterThan(50), "The relief must actually be active.");
                // A neighbourhood in the designed through-hole remains empty, including the chunk seam.
                for (int y = 124; y < 132; y++) for (int x = 124; x < 132; x++)
                        Assert.That(split.GetPixel(x, y).r + split.GetPixel(x, y).g + split.GetPixel(x, y).b, Is.LessThan(.01f));
                Assert.That(a.Zip(shadow.GetPixels(), (x, y) => Mathf.Abs(x.r - y.r)).Average(), Is.LessThan(.02f), "No false whole-face self-shadowing.");
            }
            finally
            {
                if (whole) Object.DestroyImmediate(whole); if (split) Object.DestroyImmediate(split);
                if (flat) Object.DestroyImmediate(flat); if (shadow) Object.DestroyImmediate(shadow);
            }
        }
    }
}
