using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace LocalModels.VoxelBridge.Tests
{
    public sealed class VoxelProductionGridRenderTests
    {
        // Isolated HDRP preview: no Test2 reload or persistent material changes.
        internal static Texture2D Render(string shaderPath, bool enabled, bool zeroStrength = false, float gridMode = 1,
            float profile = 0, float depth = .025f, float bevelWidth = .06f, int geometry = 0, float orbit = 0,
            float pom = 0, float pomMaxDepth = .003f, float variation = 0, int surfaceId = 0)
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(shaderPath);
            Assert.That(shader, Is.Not.Null);
            Assert.That(ShaderUtil.ShaderHasError(shader), Is.False);
            var material = new Material(shader);
            var colors = new Texture2D(256, 1, TextureFormat.RGBA32, false, true);
            var surfaces = new Texture2D(2, variation > 0 ? 2 : 1, TextureFormat.RGBA32, false, true) { filterMode = FilterMode.Point };
            Mesh ownedMesh = null;
            var preview = new PreviewRenderUtility();
            var previous = RenderTexture.active;
            RenderTexture rt = null;
            Texture2D result = null;
            try
            {
                colors.SetPixels(Enumerable.Repeat(new Color(.6f, .42f, .25f, 1), 256).ToArray()); colors.Apply();
                surfaces.SetPixel(0, 0, new Color(0, .5f, 0, 1)); surfaces.SetPixel(1, 0, new Color(0, .5f, 0, 1));
                if (variation > 0)
                {
                    surfaces.SetPixel(0, 1, Color.clear);
                    surfaces.SetPixel(1, 1, new Color(variation / .25f, 0, 0, 0));
                }
                surfaces.Apply();
                material.SetTexture("_PaletteColor", colors); material.SetTexture("_PaletteSurface", surfaces);
                material.SetFloat("_EmissionIntensity", 1);
                if (material.HasProperty("_GridEnabled"))
                {
                    material.SetFloat("_GridEnabled", enabled ? 1 : 0);
                    material.SetFloat("_GridCellSize", .0625f);
                    material.SetFloat("_GridAnchor", 1); material.SetFloat("_GridMultiscale", gridMode);
                    material.SetFloat("_GridNormalStrength", zeroStrength ? 0 : .16f);
                    material.SetFloat("_GridRoughnessStrength", zeroStrength ? 0 : .1f);
                    if (material.HasProperty("_GridProfile"))
                    {
                        material.SetFloat("_GridProfile", profile); material.SetFloat("_GridJointDepth", depth);
                        material.SetFloat("_GridBevelWidth", bevelWidth);
                        material.SetFloat("_GridPomEnabled", pom); material.SetFloat("_GridPomMaxDepth", pomMaxDepth);
                        if (profile > .5f)
                        {
                            material.SetFloat("_GridJointWidth", .04f);
                            material.SetFloat("_GridNormalStrength", zeroStrength ? 0 : 1);
                        }
                    }
                }
                HDMaterial.ValidateMaterial(material);
                preview.camera.fieldOfView = 35;
                preview.camera.nearClipPlane = .1f; preview.camera.farClipPlane = 20;
                preview.camera.clearFlags = CameraClearFlags.SolidColor;
                preview.camera.backgroundColor = new Color(.12f, .13f, .15f);
                preview.camera.aspect = 1;
                preview.camera.transform.SetPositionAndRotation(new Vector3(0, 0, -2.5f), Quaternion.identity);
                if (geometry > 0)
                {
                    var position = Quaternion.Euler(0, orbit, 0) * new Vector3(0, geometry == 2 ? 1 : .3f, -2.5f);
                    preview.camera.transform.SetPositionAndRotation(position, Quaternion.LookRotation(-position));
                }
                preview.ambientColor = new Color(.25f, .25f, .25f);
                for (int i = 0; i < preview.lights.Length; i++)
                {
                    var light = preview.lights[i];
                    if (!light.GetComponent<HDAdditionalLightData>()) light.gameObject.AddComponent<HDAdditionalLightData>();
                    light.lightUnit = LightUnit.Lux; light.intensity = i == 0 ? 4 : 2;
                    light.color = Color.white; light.useColorTemperature = false; light.shadows = LightShadows.None;
                    light.transform.rotation = Quaternion.Euler(i == 0 ? new Vector3(25, 25, 0) : new Vector3(335, 315, 0));
                }
                Texture rendered = null;
                var cube = Resources.GetBuiltinResource<Mesh>("Cube.fbx");
                // Built-in meshes have no semantic UV3. Supply IDs even for surface zero.
                ownedMesh = Object.Instantiate(cube); cube = ownedMesh;
                cube.uv4 = Enumerable.Repeat(new Vector2(surfaceId, 0), cube.vertexCount).ToArray();
                // Warm up the HDRP preview scene before capturing the result.
                for (int i = 0; i < 3; i++)
                {
                    preview.BeginPreview(new Rect(0, 0, 256, 256), GUIStyle.none);
                    try
                    {
                        var matrix = geometry == 0 ? Matrix4x4.Rotate(Quaternion.Euler(10, 25, 0)) :
                            Matrix4x4.Scale(geometry == 1 ? new Vector3(1.5f, 1.5f, .125f) : new Vector3(1.5f, .0625f, 1.5f));
                        preview.DrawMesh(cube, matrix, material, 0);
                        preview.Render(true, false);
                    }
                    finally { rendered = preview.EndPreview(); }
                }
                rt = RenderTexture.GetTemporary(256, 256, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(rendered, rt); RenderTexture.active = rt;
                result = new Texture2D(256, 256, TextureFormat.RGBA32, false);
                result.ReadPixels(new Rect(0, 0, 256, 256), 0, 0); result.Apply();
                return result;
            }
            catch { if (result) Object.DestroyImmediate(result); throw; }
            finally
            {
                RenderTexture.active = previous;
                if (rt) RenderTexture.ReleaseTemporary(rt);
                preview.Cleanup();
                Object.DestroyImmediate(material); Object.DestroyImmediate(colors); Object.DestroyImmediate(surfaces);
                if (ownedMesh) Object.DestroyImmediate(ownedMesh);
            }
        }

        [Test]
        public void Production_GridIsVisibleAndMatchesPrototypeWithoutChangingFlatReference()
        {
            Texture2D off = null, on = null, zero = null, prototype = null;
            try
            {
                off = Render(VoxelProductionExporter.ShaderPath, false);
                on = Render(VoxelProductionExporter.ShaderPath, true);
                zero = Render(VoxelProductionExporter.ShaderPath, true, true);
                prototype = Render("Assets/VoxelBridge/Shaders/VoxelGridPrototype.shadergraph", true);
                var a = off.GetPixels(); var b = on.GetPixels();
                Assert.That(a.Zip(b, (x, y) => Vector4.Distance(x, y)).Count(d => d > .02f), Is.GreaterThan(200));
                Assert.That(b.Zip(prototype.GetPixels(), (x, y) => Vector4.Distance(x, y)).Max(), Is.LessThan(.01f));
                Assert.That(a.Zip(zero.GetPixels(), (x, y) => Vector4.Distance(x, y)).Max(), Is.LessThan(.01f));
            }
            finally
            {
                if (off) Object.DestroyImmediate(off); if (on) Object.DestroyImmediate(on);
                if (zero) Object.DestroyImmediate(zero); if (prototype) Object.DestroyImmediate(prototype);
            }
        }

        [Test]
        public void Production_DistanceModeRendersFineGridInsideNearRange()
        {
            Texture2D expected = null, actual = null;
            try
            {
                expected = Render(VoxelProductionExporter.ShaderPath, true, gridMode: 0);
                actual = Render(VoxelProductionExporter.ShaderPath, true, gridMode: 2);
                Assert.That(expected.GetPixels().Zip(actual.GetPixels(), (x, y) => Vector4.Distance(x, y)).Max(), Is.LessThan(.01f));
            }
            finally
            {
                if (expected) Object.DestroyImmediate(expected);
                if (actual) Object.DestroyImmediate(actual);
            }
        }

        [TestCase(1)]
        [TestCase(2)]
        public void Production_BevelDepthChangesWallAndFloorWithoutChangingGeometry(int geometry)
        {
            Texture2D flat = null, bevel = null;
            try
            {
                flat = Render(VoxelProductionExporter.ShaderPath, true, gridMode: 2, profile: 1, depth: 0, geometry: geometry);
                bevel = Render(VoxelProductionExporter.ShaderPath, true, gridMode: 2, profile: 1, geometry: geometry);
                var a = flat.GetPixels(); var b = bevel.GetPixels();
                Assert.That(a.Zip(b, (x, y) => Vector4.Distance(x, y)).Count(d => d > .02f), Is.GreaterThan(100));
                CollectionAssert.AreEqual(a.Select(p => p.a).ToArray(), b.Select(p => p.a).ToArray());
            }
            finally
            {
                if (flat) Object.DestroyImmediate(flat); if (bevel) Object.DestroyImmediate(bevel);
            }
        }

        [TestCase(1)]
        [TestCase(2)]
        public void Production_PomChangesObliqueShadingButPreservesSilhouette(int geometry)
        {
            Texture2D off = null, on = null;
            try
            {
                off = Render(VoxelProductionExporter.ShaderPath, true, gridMode: 0, profile: 1, geometry: geometry, orbit: 45, depth: .08f);
                on = Render(VoxelProductionExporter.ShaderPath, true, gridMode: 0, profile: 1, geometry: geometry, orbit: 45, depth: .08f, pom: 1);
                var a = off.GetPixels(); var b = on.GetPixels();
                Assert.That(a.Zip(b, (x, y) => Vector4.Distance(x, y)).Count(d => d > .01f), Is.GreaterThan(30));
                CollectionAssert.AreEqual(a.Select(p => p.a).ToArray(), b.Select(p => p.a).ToArray());
            }
            finally { if (off) Object.DestroyImmediate(off); if (on) Object.DestroyImmediate(on); }
        }

        [Test]
        public void Production_HeightVariationIsSelectedBySurfaceId()
        {
            Texture2D baseline = null, unchanged = null, varied = null;
            try
            {
                baseline = Render(VoxelProductionExporter.ShaderPath, true, gridMode: 0, profile: 1, pom: 1, geometry: 1, orbit: 45);
                unchanged = Render(VoxelProductionExporter.ShaderPath, true, gridMode: 0, profile: 1, pom: 1, geometry: 1, orbit: 45, variation: .08f);
                varied = Render(VoxelProductionExporter.ShaderPath, true, gridMode: 0, profile: 1, pom: 1, geometry: 1, orbit: 45, variation: .08f, surfaceId: 1);
                var a = baseline.GetPixels(); var b = unchanged.GetPixels(); var c = varied.GetPixels();
                Assert.That(a.Zip(b, (x,y) => Vector4.Distance(x,y)).Max(), Is.LessThan(.01f));
                Assert.That(b.Zip(c, (x,y) => Vector4.Distance(x,y)).Count(d => d > .01f), Is.GreaterThan(100));
            }
            finally
            {
                if (baseline) Object.DestroyImmediate(baseline); if (unchanged) Object.DestroyImmediate(unchanged); if (varied) Object.DestroyImmediate(varied);
            }
        }
    }
}
