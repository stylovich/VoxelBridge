using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;

namespace LocalModels.VoxelBridge.Tests
{
    public sealed class VoxelPomShadowTests
    {
        // Isolated rendering; no persistent scene, material, pipeline or light changes.
        internal static Texture2D Render(bool depthWrite, bool castSelf, int resolution = 2048,
            bool blocker = false, bool directional = false, float orbit = 0,
            float variation = .04f, float lightAngle = 20)
        {
            var preview = new PreviewRenderUtility();
            Material material = null;
            Texture2D colors = null, surfaces = null, result = null;
            Mesh mesh = null;
            VolumeProfile profile = null;
            RenderTexture copy = null;
            var previous = RenderTexture.active;
            try
            {
                var shader = AssetDatabase.LoadAssetAtPath<Shader>(VoxelProductionGridRenderTests.DepthShaderPath);
                Assert.That(shader && !ShaderUtil.ShaderHasError(shader), Is.True);
                material = new Material(shader);
                colors = new Texture2D(256, 1, TextureFormat.RGBA32, false, true);
                colors.SetPixels(Enumerable.Repeat(new Color(.4f, .4f, .4f, 1), 256).ToArray()); colors.Apply();
                surfaces = new Texture2D(256, 2, TextureFormat.RGBA32, false, true) { filterMode = FilterMode.Point };
                surfaces.SetPixels(Enumerable.Repeat(new Color(0, .3f, 0, 1), 256)
                    .Concat(Enumerable.Repeat(new Color(variation / .25f, 0, 0, 0), 256)).ToArray()); surfaces.Apply();
                material.SetTexture("_PaletteColor", colors); material.SetTexture("_PaletteSurface", surfaces);
                material.SetFloat("_EmissionIntensity", 0);
                preview.ambientColor = Color.black;
                material.SetFloat("_GridEnabled", 1); material.SetFloat("_GridAnchor", 1);
                material.SetFloat("_GridCellSize", .0625f); material.SetFloat("_GridMultiscale", 0);
                material.SetFloat("_GridProfile", 1); material.SetFloat("_GridJointWidth", .01f);
                material.SetFloat("_GridBevelWidth", .03f); material.SetFloat("_GridJointDepth", .025f);
                material.SetFloat("_GridNormalStrength", .255f); material.SetFloat("_GridPomEnabled", 1);
                material.SetFloat("_GridPomMaxDepth", .01f); material.SetFloat("_DepthOffsetEnable", depthWrite ? 1 : 0);
                material.SetFloat("_ConservativeDepthOffsetEnable", 1);
                HDMaterial.ValidateMaterial(material);
                mesh = Object.Instantiate(Resources.GetBuiltinResource<Mesh>("Cube.fbx"));
                mesh.uv4 = Enumerable.Repeat(Vector2.zero, mesh.vertexCount).ToArray();
                var wall = new GameObject("POM shadow fixture");
                wall.AddComponent<MeshFilter>().sharedMesh = mesh;
                var renderer = wall.AddComponent<MeshRenderer>(); renderer.sharedMaterial = material;
                renderer.lightProbeUsage = LightProbeUsage.Off; renderer.reflectionProbeUsage = ReflectionProbeUsage.Off;
                renderer.shadowCastingMode = castSelf ? ShadowCastingMode.On : ShadowCastingMode.Off;
                wall.transform.localScale = new Vector3(1.5f, 1.5f, .05f); preview.AddSingleGO(wall);
                if (blocker)
                {
                    var other = new GameObject("External shadow caster");
                    other.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var r = other.AddComponent<MeshRenderer>(); r.sharedMaterial = material;
                    r.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
                    other.transform.SetPositionAndRotation(new Vector3(0, 0, -.35f), Quaternion.identity);
                    other.transform.localScale = new Vector3(.2f, .6f, .1f); preview.AddSingleGO(other);
                }
                var camera = preview.camera;
                // Ordinary material previews explicitly disable shadows in HDRP.
                camera.cameraType = CameraType.Game; camera.fieldOfView = 50;
                camera.nearClipPlane = .1f; camera.farClipPlane = 10;
                var position = Quaternion.Euler(0, orbit, 0) * new Vector3(0, 0, -1.8f);
                camera.transform.SetPositionAndRotation(position, Quaternion.LookRotation(-position));
                var data = camera.gameObject.AddComponent<HDAdditionalCameraData>();
                data.clearColorMode = HDAdditionalCameraData.ClearColorMode.Color; data.backgroundColorHDR = Color.black;
                var volumeObject = new GameObject("Shadow test volume") { layer = 31 };
                preview.AddSingleGO(volumeObject);
                var volume = volumeObject.AddComponent<Volume>(); volume.isGlobal = true; volume.priority = 10000;
                profile = ScriptableObject.CreateInstance<VolumeProfile>(); volume.sharedProfile = profile;
                var indirect = profile.Add<IndirectLightingController>();
                indirect.indirectDiffuseLightingMultiplier.Override(0);
                indirect.reflectionLightingMultiplier.Override(0);
                profile.Add<Exposure>().fixedExposure.Override(0);
                profile.TryGet<Exposure>(out var exposure); exposure.mode.Override(ExposureMode.Fixed);
                var shadows = profile.Add<HDShadowSettings>();
                shadows.maxShadowDistance.Override(4); shadows.cascadeShadowSplitCount.Override(1);
                data.volumeLayerMask = 1 << 31; data.customRenderingSettings = true;
                foreach (var field in new[]{FrameSettingsField.Postprocess,
                    FrameSettingsField.AtmosphericScattering,FrameSettingsField.CustomPass,FrameSettingsField.ContactShadows,
                    FrameSettingsField.AdaptiveProbeVolume,FrameSettingsField.ReflectionProbe,FrameSettingsField.SSR,FrameSettingsField.SSAO})
                { data.renderingPathCustomFrameSettingsOverrideMask.mask[(uint)field] = true; data.renderingPathCustomFrameSettings.SetEnabled(field, false); }
                data.renderingPathCustomFrameSettingsOverrideMask.mask[(uint)FrameSettingsField.ShadowMaps] = true;
                data.renderingPathCustomFrameSettings.SetEnabled(FrameSettingsField.ShadowMaps, true);
                data.renderingPathCustomFrameSettingsOverrideMask.mask[(uint)FrameSettingsField.ExposureControl] = true;
                data.renderingPathCustomFrameSettings.SetEnabled(FrameSettingsField.ExposureControl, true);
                for (int i = 0; i < preview.lights.Length; i++)
                {
                    var light = preview.lights[i]; light.enabled = i == 0;
                    // PreviewRenderUtility re-enables Light components during Render.
                    if (i != 0) { light.gameObject.SetActive(false); continue; }
                    var hd = light.gameObject.AddComponent<HDAdditionalLightData>();
                    light.type = directional ? LightType.Directional : LightType.Spot; light.spotAngle = 90; light.range = 10;
                    light.lightUnit = directional ? LightUnit.Lux : LightUnit.Candela; light.intensity = directional ? 2 : 5;
                    light.color = Color.white; light.useColorTemperature = false; light.shadows = LightShadows.Soft;
                    light.transform.position = new Vector3(-1.4f * Mathf.Tan(lightAngle * Mathf.Deg2Rad), .2f, -1.4f);
                    light.transform.rotation = Quaternion.LookRotation(-light.transform.position);
                    hd.normalBias = .75f; hd.slopeBias = .5f; hd.SetShadowResolution(resolution);
                    hd.SetShadowResolutionOverride(true);
                }
                Texture rendered = null;
                for (int i = 0; i < 3; i++)
                {
                    preview.BeginPreview(new Rect(0, 0, 256, 256), GUIStyle.none);
                    try
                    {
                        preview.Render(true, false);
                    }
                    finally { rendered = preview.EndPreview(); }
                }
                copy = RenderTexture.GetTemporary(256, 256, 0, RenderTextureFormat.ARGB32);
                Graphics.Blit(rendered, copy); RenderTexture.active = copy;
                result = new Texture2D(256, 256, TextureFormat.RGBA32, false);
                result.ReadPixels(new Rect(0, 0, 256, 256), 0, 0); result.Apply(); return result;
            }
            catch { if (result) Object.DestroyImmediate(result); throw; }
            finally
            {
                RenderTexture.active = previous; if (copy) RenderTexture.ReleaseTemporary(copy);
                preview.Cleanup(); if (material) Object.DestroyImmediate(material);
                if (colors) Object.DestroyImmediate(colors); if (surfaces) Object.DestroyImmediate(surfaces);
                if (mesh) Object.DestroyImmediate(mesh);
                if (profile) { foreach (var component in profile.components) Object.DestroyImmediate(component); Object.DestroyImmediate(profile); }
            }
        }

        [TestCase(2048, false, 0)]
        [TestCase(4096, false, 0)]
        [TestCase(2048, true, 0)]
        [TestCase(4096, true, 0)]
        [TestCase(4096, false, 40)]
        [TestCase(4096, true, 40)]
        public void DepthRelief_DoesNotBlackenItsOwnCellFaces(int resolution, bool directional, float orbit)
        {
            Texture2D reference = null, actual = null;
            try
            {
                reference = Render(true, false, resolution, directional: directional, orbit: orbit);
                actual = Render(true, true, resolution, directional: directional, orbit: orbit);
                var a = reference.GetPixels();
                var b = actual.GetPixels();
                Assert.That(a.Count(p => p.r > .1f && p.r < .95f), Is.GreaterThan(20000), "A lit, unsaturated reference is required.");
                Assert.That(a.Zip(b, (x, y) => Mathf.Abs(x.r - y.r)).Average(), Is.LessThan(.01f),
                    "A flat caster must not shadow the recessed POM receiver as black cells.");
            }
            finally { if (reference) Object.DestroyImmediate(reference); if (actual) Object.DestroyImmediate(actual); }
        }

        [TestCase(false)]
        [TestCase(true)]
        public void DepthRelief_StillReceivesExternalShadows(bool directional)
        {
            Texture2D reference = null, actual = null;
            try
            {
                reference = Render(true, true, 4096, directional: directional);
                actual = Render(true, true, 4096, blocker: true, directional: directional);
                Assert.That(reference.GetPixels().Zip(actual.GetPixels(), (x, y) => x.r - y.r > .1f).Count(d => d),
                    Is.GreaterThan(1000), "Fixing self-shadow acne must not disable shadow reception or casting.");
            }
            finally { if (reference) Object.DestroyImmediate(reference); if (actual) Object.DestroyImmediate(actual); }
        }

        [TestCase(2048, 55)]
        [TestCase(4096, 55)]
        [TestCase(2048, 65)]
        [TestCase(4096, 65)]
        public void DepthRelief_MaxVariationKeepsFlatInteriorsFreeOfAcne(int resolution, float angle)
        {
            Texture2D reference = null, actual = null;
            try
            {
                reference = Render(true, false, resolution, directional: true, variation: .25f, lightAngle: angle);
                actual = Render(true, true, resolution, directional: true, variation: .25f, lightAngle: angle);
                int compared = 0;
                float error = 0;
                for (int y = 0; y < 256; y++)
                for (int x = 0; x < 256; x++)
                {
                    // Intersect the fixed fixture camera's ray with the wall's front plane.
                    // Compare only cell centres: deeper relief can legitimately shadow neighbouring bevels.
                    float extent = 1.775f * Mathf.Tan(25 * Mathf.Deg2Rad);
                    float cellX = Mathf.Repeat(((x + .5f) / 256 * 2 - 1) * extent / .0625f, 1);
                    float cellY = Mathf.Repeat(((y + .5f) / 256 * 2 - 1) * extent / .0625f, 1);
                    if (cellX < .45f || cellX > .55f || cellY < .45f || cellY > .55f) continue;
                    float expected = reference.GetPixel(x, y).r;
                    if (expected <= .1f || expected >= .95f) continue;
                    error += Mathf.Abs(expected - actual.GetPixel(x, y).r);
                    compared++;
                }
                Assert.That(compared, Is.GreaterThan(250), "Require lit, unsaturated cell interiors.");
                Assert.That(error / compared, Is.LessThan(.001f), "The shadow's lateral cap must not flatten recessed cell tops.");
            }
            finally { if (reference) Object.DestroyImmediate(reference); if (actual) Object.DestroyImmediate(actual); }
        }
    }
}
