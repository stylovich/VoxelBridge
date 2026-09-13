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
        internal static Texture2D Render(string shaderPath, bool enabled, bool zeroStrength = false, float gridMode = 1)
        {
            var shader = AssetDatabase.LoadAssetAtPath<Shader>(shaderPath);
            Assert.That(shader, Is.Not.Null);
            Assert.That(ShaderUtil.ShaderHasError(shader), Is.False);
            var material = new Material(shader);
            var colors = new Texture2D(256, 1, TextureFormat.RGBA32, false, true);
            var surfaces = new Texture2D(1, 1, TextureFormat.RGBA32, false, true);
            var preview = new PreviewRenderUtility();
            var previous = RenderTexture.active;
            RenderTexture rt = null;
            Texture2D result = null;
            try
            {
                colors.SetPixels(Enumerable.Repeat(new Color(.6f, .42f, .25f, 1), 256).ToArray()); colors.Apply();
                surfaces.SetPixel(0, 0, new Color(0, .5f, 0, 1)); surfaces.Apply();
                material.SetTexture("_PaletteColor", colors); material.SetTexture("_PaletteSurface", surfaces);
                material.SetFloat("_EmissionIntensity", 1);
                if (material.HasProperty("_GridEnabled"))
                {
                    material.SetFloat("_GridEnabled", enabled ? 1 : 0);
                    material.SetFloat("_GridCellSize", .0625f);
                    material.SetFloat("_GridAnchor", 1); material.SetFloat("_GridMultiscale", gridMode);
                    material.SetFloat("_GridNormalStrength", zeroStrength ? 0 : .16f);
                    material.SetFloat("_GridRoughnessStrength", zeroStrength ? 0 : .1f);
                }
                HDMaterial.ValidateMaterial(material);
                preview.camera.fieldOfView = 35;
                preview.camera.nearClipPlane = .1f; preview.camera.farClipPlane = 20;
                preview.camera.clearFlags = CameraClearFlags.SolidColor;
                preview.camera.backgroundColor = new Color(.12f, .13f, .15f);
                preview.camera.aspect = 1;
                preview.camera.transform.SetPositionAndRotation(new Vector3(0, 0, -2.5f), Quaternion.identity);
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
                // Warm up the HDRP preview scene before capturing the result.
                for (int i = 0; i < 3; i++)
                {
                    preview.BeginPreview(new Rect(0, 0, 256, 256), GUIStyle.none);
                    try
                    {
                        preview.DrawMesh(cube, Matrix4x4.Rotate(Quaternion.Euler(10, 25, 0)), material, 0);
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
    }
}
