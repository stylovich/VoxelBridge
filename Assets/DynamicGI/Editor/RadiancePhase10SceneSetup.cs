using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using DynamicGI.Contributors;
using DynamicGI.Geometry;
using DynamicGI.Occlusion;
using DynamicGI.Radiance;
using DynamicGI.Rendering;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.SceneManagement;

namespace DynamicGI.Editor
{
    /// <summary>Configures and validates material/shader sampling integration in TestGI.</summary>
    public static class RadiancePhase10SceneSetup
    {
        private const string ScenePath = "Assets/Scenes/TestGI.unity";
        private const string RenderingRootName = "Dynamic GI Phase 10 - Rendering";
        private const string CompositeShaderPath = "Assets/DynamicGI/Shaders/DynamicGIHDRPComposite.shader";
        private const string SamplingShaderPath = "Assets/DynamicGI/Shaders/RadianceSamplingValidation.compute";
        private static readonly Vector3 ExistingIndirect = new(0.2f, 0.35f, 0.5f);

        [StructLayout(LayoutKind.Sequential)]
        private struct ProviderGpuResult
        {
            public Vector4 ControlledDynamicAndAccessibility;
            public Vector4 FinalIndirect;
        }

        [MenuItem("Tools/Dynamic GI/Phase 10/Configure TestGI Material Sampling")]
        public static void ConfigureTestGI()
        {
            RadiancePhase9SceneSetup.ConfigureTestGI();
            Scene scene = SceneManager.GetActiveScene();
            DestroyGeneratedRoot(scene, RenderingRootName);

            Shader compositeShader = AssetDatabase.LoadAssetAtPath<Shader>(CompositeShaderPath);
            if (compositeShader == null)
                throw new InvalidOperationException($"Missing Phase 10 shader at {CompositeShaderPath}.");

            GameObject root = new(RenderingRootName);
            SceneManager.MoveGameObjectToScene(root, scene);
            DynamicGIShaderGlobals controls = root.AddComponent<DynamicGIShaderGlobals>();
            controls.Configure(
                IndirectLightingProviderMode.ExistingPlusDynamic,
                0.35f,
                0.5f,
                1f,
                1f,
                0.625f,
                0.4f,
                true,
                0f);

            CustomPassVolume volume = root.AddComponent<CustomPassVolume>();
            volume.isGlobal = true;
            volume.priority = 0f;
            volume.injectionPoint = CustomPassInjectionPoint.BeforeTransparent;
            volume.targetCamera = null;
            volume.customPasses.Clear();

            DynamicGIHDRPCompositePass pass = new()
            {
                name = "Dynamic GI diffuse additive bridge",
                enabled = true,
                targetColorBuffer = CustomPass.TargetBuffer.Camera,
                targetDepthBuffer = CustomPass.TargetBuffer.None,
                clearFlags = ClearFlag.None,
            };
            pass.Configure(compositeShader, true, false);
            volume.customPasses.Add(pass);

            EditorUtility.SetDirty(root);
            EditorUtility.SetDirty(controls);
            EditorUtility.SetDirty(volume);
            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene))
                throw new InvalidOperationException($"Could not save {ScenePath}.");

            Debug.Log(
                "DYNAMIC_GI_PHASE10_TESTGI_CONFIGURED | provider=Existing+Dynamic | strength=0.35 | " +
                "occlusion=separate ambient accessibility | HDRP bridge=BeforeTransparent/opaque/additive | " +
                "APV=untouched");
        }

        [MenuItem("Tools/Dynamic GI/Phase 10/Validate TestGI Material Sampling")]
        public static void ValidateTestGI()
        {
            Scene scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            WorldGeometryField geometry = FindSingle<WorldGeometryField>(scene);
            WorldSkyVisibilityField sky = FindSingle<WorldSkyVisibilityField>(scene);
            WorldRadianceClipmap clipmap = FindSingle<WorldRadianceClipmap>(scene);
            DynamicGIShaderGlobals controls = FindSingle<DynamicGIShaderGlobals>(scene);
            CustomPassVolume volume = FindSingle<CustomPassVolume>(scene, RenderingRootName);
            Transform sampleMarker = FindNamedTransform(scene, "Emissive Visible Probe Marker");

            ValidateBridge(volume, controls);
            geometry.RebuildAll();
            geometry.ProcessAllDirtyNow();
            sky.RebuildAll();
            sky.ProcessAllDirtyNow();
            clipmap.RebuildAll();
            clipmap.ProcessAllDirtyNow();
            controls.PublishNow();

            ComputeShader samplingShader = AssetDatabase.LoadAssetAtPath<ComputeShader>(SamplingShaderPath);
            if (samplingShader == null)
                throw new InvalidOperationException($"Missing Phase 10 validation shader at {SamplingShaderPath}.");
            int kernel = samplingShader.FindKernel("QueryIndirectLightingProvider");
            if (!clipmap.BindSamplingResources(samplingShader, kernel) || !sky.BindSamplingResources(samplingShader, kernel))
                throw new InvalidOperationException("Could not bind radiance/sky resources to the Phase 10 provider validation.");

            Vector3[] positions = { sampleMarker.position };
            Vector3[] normals = { Vector3.back };
            Vector3[] existing = { ExistingIndirect };
            using GraphicsBuffer positionBuffer = new(GraphicsBuffer.Target.Structured, 1, sizeof(float) * 3);
            using GraphicsBuffer normalBuffer = new(GraphicsBuffer.Target.Structured, 1, sizeof(float) * 3);
            using GraphicsBuffer existingBuffer = new(GraphicsBuffer.Target.Structured, 1, sizeof(float) * 3);
            using GraphicsBuffer resultBuffer = new(GraphicsBuffer.Target.Structured, 1, Marshal.SizeOf<ProviderGpuResult>());
            positionBuffer.SetData(positions);
            normalBuffer.SetData(normals);
            existingBuffer.SetData(existing);
            samplingShader.SetBuffer(kernel, "_DynamicGISamplePositions", positionBuffer);
            samplingShader.SetBuffer(kernel, "_DynamicGISampleNormals", normalBuffer);
            samplingShader.SetBuffer(kernel, "_DynamicGIExistingIndirect", existingBuffer);
            samplingShader.SetBuffer(kernel, "_DynamicGIProviderResults", resultBuffer);
            samplingShader.SetInt("_DynamicGISampleCount", 1);
            samplingShader.SetFloat("_DynamicGI_Strength", controls.DynamicGIStrength);
            samplingShader.SetFloat("_DynamicGI_OcclusionStrength", controls.OcclusionStrength);
            samplingShader.SetFloat("_DynamicGI_IndirectSaturation", controls.IndirectSaturation);
            samplingShader.SetFloat("_DynamicGI_IndirectIntensity", controls.IndirectIntensity);
            samplingShader.SetFloat("_DynamicGI_SurfaceNormalBias", controls.SurfaceNormalBias);
            samplingShader.SetInt("_DynamicGI_ScreenSpaceBridgeEnabled", 1);

            ProviderGpuResult combined = DispatchProvider(
                samplingShader, kernel, resultBuffer, IndirectLightingProviderMode.ExistingPlusDynamic);
            ProviderGpuResult dynamicOnly = DispatchProvider(
                samplingShader, kernel, resultBuffer, IndirectLightingProviderMode.DynamicOnly);
            ProviderGpuResult existingOnly = DispatchProvider(
                samplingShader, kernel, resultBuffer, IndirectLightingProviderMode.ExistingOnly);
            ProviderGpuResult disabled = DispatchProvider(
                samplingShader, kernel, resultBuffer, IndirectLightingProviderMode.Disabled);

            Vector3 dynamicValue = combined.ControlledDynamicAndAccessibility;
            if (Luminance(dynamicValue) <= 0.001f)
                throw new InvalidOperationException("The Phase 10 material sample received no Dynamic GI at the emissive marker.");
            AssertNear((Vector3)combined.FinalIndirect, ExistingIndirect + dynamicValue, "Existing + Dynamic");
            AssertNear((Vector3)dynamicOnly.FinalIndirect, dynamicValue, "Dynamic only");
            AssertNear((Vector3)existingOnly.FinalIndirect, ExistingIndirect, "Existing only");
            AssertNear((Vector3)disabled.FinalIndirect, Vector3.zero, "Disabled");

            float accessibility = combined.ControlledDynamicAndAccessibility.w;
            float minimumAccessibility = 1f - controls.OcclusionStrength - 0.002f;
            if (accessibility < minimumAccessibility || accessibility > 1.002f)
            {
                throw new InvalidOperationException(
                    $"Ambient accessibility {accessibility:0.0000} is outside the configured blend range [{minimumAccessibility:0.0000}, 1].");
            }

            Debug.Log(
                $"DYNAMIC_GI_PHASE10_VALIDATION_PASSED | rawProviderRGB={Format(dynamicValue)} | " +
                $"existingRGB={Format(ExistingIndirect)} | combinedRGB={Format(combined.FinalIndirect)} | " +
                $"accessibility={accessibility:0.000} | modes=4/4 | APV/existing preserved additively | " +
                $"bridge={volume.injectionPoint}/CameraColor | shader={((DynamicGIHDRPCompositePass)volume.customPasses[0]).CompositeShader.name}");
        }

        [MenuItem("Tools/Dynamic GI/Phase 10/Diagnose TestGI Surface Sampling")]
        public static void DiagnoseTestGISurfaceSampling()
        {
            Scene scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            WorldGeometryField geometry = FindSingle<WorldGeometryField>(scene);
            WorldSkyVisibilityField sky = FindSingle<WorldSkyVisibilityField>(scene);
            WorldRadianceClipmap clipmap = FindSingle<WorldRadianceClipmap>(scene);
            GIEmissiveContributor contributor = FindSingle<GIEmissiveContributor>(scene);
            Light sun = FindDirectionalSun(scene);
            Camera camera = FindSingle<Camera>(scene);

            bool originalSunEnabled = sun.enabled;
            Quaternion originalSunRotation = sun.transform.rotation;
            Color originalSunColor = sun.color;
            float originalSunIntensity = sun.intensity;
            bool originalEmission = contributor.Contributes;
            SerializedObject serializedClipmap = new(clipmap);
            SerializedProperty propagationIterations = serializedClipmap.FindProperty("propagationIterations");
            int originalPropagationIterations = propagationIterations.intValue;

            try
            {
                contributor.SetContributionEnabled(false);
                propagationIterations.intValue = 10;
                serializedClipmap.ApplyModifiedPropertiesWithoutUndo();
                sun.enabled = true;
                sun.color = new Color(1f, 0.9f, 0.75f);
                sun.intensity = 130000f;
                sun.transform.rotation = Quaternion.LookRotation(new Vector3(-1f, -0.15f, 0f).normalized, Vector3.up);

                geometry.RebuildAll();
                geometry.ProcessAllDirtyNow();
                sky.RebuildAll();
                sky.ProcessAllDirtyNow();
                clipmap.ForceLightingRefresh();
                clipmap.ProcessAllDirtyNow();

                Vector3[] positions =
                {
                    new(0f, 4.875f, 0f), // exact underside of the ceiling mesh
                    new(0f, 4.25f, 0f),  // first stable interior ceiling probe row
                    new(0f, 5.25f, 0f),  // outside, above the ceiling
                    new(0f, 0.125f, 0f), // exact top of the floor mesh
                    new(0f, 0.75f, 0f),  // first stable interior floor probe row
                    new(0f, -0.25f, 0f), // outside, below the floor
                    new(0f, 4.875f, 3.875f), // ceiling/north-wall junction
                    new(0f, 4.25f, 3.25f),   // matching interior junction sample
                };
                Vector3[] normals =
                {
                    Vector3.down,
                    Vector3.down,
                    Vector3.down,
                    Vector3.up,
                    Vector3.up,
                    Vector3.up,
                    Vector3.down,
                    Vector3.down,
                };
                Vector4[] samples = QueryRawDynamicGI(clipmap, positions, normals, "QueryDynamicGI", 0f);
                Vector4[] surfaceSamples = QueryRawDynamicGI(clipmap, positions, normals, "QuerySurfaceDynamicGI", 0.625f);
                Vector3[] bridgePositions = (Vector3[])positions.Clone();
                for (int i = 0; i < bridgePositions.Length; i++)
                {
                    Vector3 toCamera = camera.transform.position - bridgePositions[i];
                    bridgePositions[i] += toCamera.sqrMagnitude > 0f ? toCamera.normalized * 0.4f : Vector3.zero;
                }
                Vector4[] bridgeSamples = QueryRawDynamicGI(
                    clipmap,
                    bridgePositions,
                    normals,
                    "QuerySurfaceDynamicGI",
                    0.625f);
                float rawCeiling = Luminance(samples[0]);
                float biasedCeiling = Luminance(surfaceSamples[0]);
                float rawEdge = Luminance(samples[6]);
                float bridgeEdge = Luminance(bridgeSamples[6]);
                float bridgeCeiling = Luminance(bridgeSamples[0]);
                if (biasedCeiling > rawCeiling * 0.35f)
                {
                    throw new InvalidOperationException(
                        $"Surface-normal bias still mixes exterior ceiling probes: raw={rawCeiling:0.000000}, biased={biasedCeiling:0.000000}.");
                }
                if (bridgeEdge > rawEdge * 0.2f)
                {
                    throw new InvalidOperationException(
                        $"HDRP view bias still leaks at the ceiling/wall junction: raw={rawEdge:0.000000}, bridge={bridgeEdge:0.000000}.");
                }
                if (bridgeCeiling < 0.02f)
                {
                    throw new InvalidOperationException(
                        $"Ten-pass solar propagation did not produce a useful ceiling rebound: L={bridgeCeiling:0.000000}.");
                }
                Debug.Log(
                    "DYNAMIC_GI_PHASE10_SURFACE_DIAGNOSTIC | " +
                    $"ceilingSurface={Format(samples[0])} L={Luminance(samples[0]):0.000000} | " +
                    $"ceilingInterior={Format(samples[1])} L={Luminance(samples[1]):0.000000} | " +
                    $"ceilingExterior={Format(samples[2])} L={Luminance(samples[2]):0.000000} | " +
                    $"floorSurface={Format(samples[3])} L={Luminance(samples[3]):0.000000} | " +
                    $"floorInterior={Format(samples[4])} L={Luminance(samples[4]):0.000000} | " +
                    $"floorExterior={Format(samples[5])} L={Luminance(samples[5]):0.000000} | " +
                    $"ceilingEdge={Format(samples[6])} L={Luminance(samples[6]):0.000000} | " +
                    $"edgeInterior={Format(samples[7])} L={Luminance(samples[7]):0.000000} | " +
                    $"biasedCeiling={Format(surfaceSamples[0])} L={Luminance(surfaceSamples[0]):0.000000} | " +
                    $"biasedFloor={Format(surfaceSamples[3])} L={Luminance(surfaceSamples[3]):0.000000} | " +
                    $"biasedEdge={Format(surfaceSamples[6])} L={Luminance(surfaceSamples[6]):0.000000} | " +
                    $"bridgeCeiling={Format(bridgeSamples[0])} L={Luminance(bridgeSamples[0]):0.000000} | " +
                    $"bridgeFloor={Format(bridgeSamples[3])} L={Luminance(bridgeSamples[3]):0.000000} | " +
                    $"bridgeEdge={Format(bridgeSamples[6])} L={Luminance(bridgeSamples[6]):0.000000}");
            }
            finally
            {
                contributor.SetContributionEnabled(originalEmission);
                serializedClipmap.Update();
                propagationIterations.intValue = originalPropagationIterations;
                serializedClipmap.ApplyModifiedPropertiesWithoutUndo();
                sun.enabled = originalSunEnabled;
                sun.transform.rotation = originalSunRotation;
                sun.color = originalSunColor;
                sun.intensity = originalSunIntensity;
                if (clipmap.IsInitialized)
                {
                    clipmap.ForceLightingRefresh();
                    clipmap.ProcessAllDirtyNow();
                }
            }
        }

        [MenuItem("Tools/Dynamic GI/Phase 10/Diagnose TestGI HDRP Bridge Scale")]
        public static void DiagnoseTestGIBridgeScale()
        {
            Scene scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            WorldGeometryField geometry = FindSingle<WorldGeometryField>(scene);
            WorldSkyVisibilityField sky = FindSingle<WorldSkyVisibilityField>(scene);
            WorldRadianceClipmap clipmap = FindSingle<WorldRadianceClipmap>(scene);
            DynamicGIShaderGlobals controls = FindSingle<DynamicGIShaderGlobals>(scene);
            Camera camera = FindSingle<Camera>(scene);

            IndirectLightingProviderMode originalMode = controls.ProviderMode;
            float originalStrength = controls.DynamicGIStrength;
            float originalOcclusion = controls.OcclusionStrength;
            float originalSaturation = controls.IndirectSaturation;
            float originalIntensity = controls.IndirectIntensity;
            float originalNormalBias = controls.SurfaceNormalBias;
            float originalViewBias = controls.HdrpViewBias;
            bool originalBridge = controls.ScreenSpaceBridgeEnabled;
            float originalAlbedoWeight = controls.HdrpAlbedoWeight;

            try
            {
                geometry.RebuildAll();
                geometry.ProcessAllDirtyNow();
                sky.RebuildAll();
                sky.ProcessAllDirtyNow();
                clipmap.RebuildAll();
                clipmap.ProcessAllDirtyNow();

                controls.Configure(
                    IndirectLightingProviderMode.DynamicOnly,
                    1f,
                    0f,
                    1f,
                    1f,
                    0.625f,
                    0.4f,
                    false,
                    0f);
                Color[] withoutBridge = CaptureLinearCamera(camera);

                controls.Configure(
                    IndirectLightingProviderMode.DynamicOnly,
                    1f,
                    0f,
                    1f,
                    1f,
                    0.625f,
                    0.4f,
                    true,
                    0f);
                Color[] withBridge = CaptureLinearCamera(camera);

                float maximum = 0f;
                double sum = 0.0;
                int positivePixels = 0;
                for (int i = 0; i < withBridge.Length; i++)
                {
                    Vector3 delta = new(
                        Mathf.Max(0f, withBridge[i].r - withoutBridge[i].r),
                        Mathf.Max(0f, withBridge[i].g - withoutBridge[i].g),
                        Mathf.Max(0f, withBridge[i].b - withoutBridge[i].b));
                    float luminance = Luminance(delta);
                    maximum = Mathf.Max(maximum, luminance);
                    sum += luminance;
                    if (luminance > 0.0001f)
                        positivePixels++;
                }

                Debug.Log(
                    $"DYNAMIC_GI_PHASE10_BRIDGE_DIAGNOSTIC | strength=1 intensity=1 | " +
                    $"maxDeltaL={maximum:0.000000} | meanDeltaL={(sum / withBridge.Length):0.000000} | " +
                    $"positivePixels={positivePixels}/{withBridge.Length}");
                if (maximum < 0.02f || positivePixels < 1000)
                {
                    throw new InvalidOperationException(
                        $"Default HDRP bridge output is still too weak: max={maximum:0.000000}, positive={positivePixels}/{withBridge.Length}.");
                }
            }
            finally
            {
                controls.Configure(
                    originalMode,
                    originalStrength,
                    originalOcclusion,
                    originalSaturation,
                    originalIntensity,
                    originalNormalBias,
                    originalViewBias,
                    originalBridge,
                    originalAlbedoWeight);
            }
        }

        private static Color[] CaptureLinearCamera(Camera camera)
        {
            RenderTexture previousTarget = camera.targetTexture;
            RenderTexture previousActive = RenderTexture.active;
            RenderTexture target = RenderTexture.GetTemporary(
                640,
                360,
                24,
                RenderTextureFormat.ARGBHalf,
                RenderTextureReadWrite.Linear);
            Texture2D readback = new(640, 360, TextureFormat.RGBAFloat, false, true);
            try
            {
                camera.targetTexture = target;
                camera.Render();
                camera.Render();
                RenderTexture.active = target;
                readback.ReadPixels(new Rect(0f, 0f, 640f, 360f), 0, 0, false);
                readback.Apply(false, false);
                return readback.GetPixels();
            }
            finally
            {
                camera.targetTexture = previousTarget;
                RenderTexture.active = previousActive;
                RenderTexture.ReleaseTemporary(target);
                UnityEngine.Object.DestroyImmediate(readback);
            }
        }

        private static Vector4[] QueryRawDynamicGI(
            WorldRadianceClipmap clipmap,
            Vector3[] positions,
            Vector3[] normals,
            string kernelName,
            float surfaceNormalBias)
        {
            ComputeShader shader = AssetDatabase.LoadAssetAtPath<ComputeShader>(SamplingShaderPath);
            if (shader == null)
                throw new InvalidOperationException($"Missing sampling diagnostic shader at {SamplingShaderPath}.");
            int kernel = shader.FindKernel(kernelName);
            if (!clipmap.BindSamplingResources(shader, kernel))
                throw new InvalidOperationException("Could not bind the radiance clipmap to the surface diagnostic.");

            using GraphicsBuffer positionBuffer = new(GraphicsBuffer.Target.Structured, positions.Length, sizeof(float) * 3);
            using GraphicsBuffer normalBuffer = new(GraphicsBuffer.Target.Structured, normals.Length, sizeof(float) * 3);
            using GraphicsBuffer resultBuffer = new(GraphicsBuffer.Target.Structured, positions.Length, sizeof(float) * 4);
            positionBuffer.SetData(positions);
            normalBuffer.SetData(normals);
            shader.SetBuffer(kernel, "_DynamicGISamplePositions", positionBuffer);
            shader.SetBuffer(kernel, "_DynamicGISampleNormals", normalBuffer);
            shader.SetBuffer(kernel, "_DynamicGISampleResults", resultBuffer);
            shader.SetInt("_DynamicGISampleCount", positions.Length);
            shader.SetFloat("_DynamicGI_SurfaceNormalBias", surfaceNormalBias);
            shader.Dispatch(kernel, Mathf.CeilToInt(positions.Length / 64f), 1, 1);

            AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(resultBuffer);
            AsyncGPUReadback.WaitAllRequests();
            if (request.hasError)
                throw new InvalidOperationException("GPU readback failed for the surface sampling diagnostic.");
            return request.GetData<Vector4>().ToArray();
        }

        private static ProviderGpuResult DispatchProvider(
            ComputeShader shader,
            int kernel,
            GraphicsBuffer resultBuffer,
            IndirectLightingProviderMode mode)
        {
            shader.SetInt("_DynamicGI_IndirectProviderMode", (int)mode);
            shader.Dispatch(kernel, 1, 1, 1);
            AsyncGPUReadbackRequest request = AsyncGPUReadback.Request(resultBuffer);
            AsyncGPUReadback.WaitAllRequests();
            if (request.hasError)
                throw new InvalidOperationException($"GPU readback failed for provider mode {mode}.");
            return request.GetData<ProviderGpuResult>()[0];
        }

        private static void ValidateBridge(CustomPassVolume volume, DynamicGIShaderGlobals controls)
        {
            if (!volume.isGlobal || volume.injectionPoint != CustomPassInjectionPoint.BeforeTransparent)
                throw new InvalidOperationException("Phase 10 bridge must be a global BeforeTransparent Custom Pass volume.");
            if (volume.customPasses.Count != 1 || volume.customPasses[0] is not DynamicGIHDRPCompositePass pass)
                throw new InvalidOperationException("Phase 10 bridge pass is missing or duplicated.");
            if (!pass.enabled || pass.targetColorBuffer != CustomPass.TargetBuffer.Camera ||
                pass.targetDepthBuffer != CustomPass.TargetBuffer.None || pass.CompositeShader == null)
                throw new InvalidOperationException("Phase 10 bridge targets or shader are not configured correctly.");
            if (!controls.ScreenSpaceBridgeEnabled)
                throw new InvalidOperationException("Phase 10 screen-space bridge is disabled.");

            ShaderMessage[] messages = ShaderUtil.GetShaderMessages(pass.CompositeShader);
            for (int i = 0; i < messages.Length; i++)
            {
                if (messages[i].severity == ShaderCompilerMessageSeverity.Error)
                    throw new InvalidOperationException($"HDRP composite shader error: {messages[i].message}");
            }
        }

        private static void AssertNear(Vector3 actual, Vector3 expected, string label)
        {
            float error = Mathf.Max(
                Mathf.Abs(actual.x - expected.x),
                Mathf.Max(Mathf.Abs(actual.y - expected.y), Mathf.Abs(actual.z - expected.z)));
            if (error > 0.0025f)
                throw new InvalidOperationException($"Provider mode '{label}' mismatch: actual={Format(actual)}, expected={Format(expected)}, error={error:0.000000}.");
        }

        private static float Luminance(Vector3 value) => Vector3.Dot(value, new Vector3(0.2126f, 0.7152f, 0.0722f));
        private static string Format(Vector3 value) => $"({value.x:0.0000},{value.y:0.0000},{value.z:0.0000})";
        private static string Format(Vector4 value) => Format((Vector3)value);

        private static T FindSingle<T>(Scene scene, string rootName = null) where T : Component
        {
            T[] values = FindComponents<T>(scene);
            if (rootName != null)
            {
                List<T> filtered = new();
                for (int i = 0; i < values.Length; i++)
                    if (values[i].transform.root.name == rootName) filtered.Add(values[i]);
                values = filtered.ToArray();
            }
            if (values.Length != 1)
                throw new InvalidOperationException($"Expected one {typeof(T).Name} in TestGI, found {values.Length}.");
            return values[0];
        }

        private static T[] FindComponents<T>(Scene scene) where T : Component
        {
            List<T> values = new();
            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
                values.AddRange(roots[i].GetComponentsInChildren<T>(true));
            return values.ToArray();
        }

        private static Transform FindNamedTransform(Scene scene, string name)
        {
            Transform[] transforms = FindComponents<Transform>(scene);
            for (int i = 0; i < transforms.Length; i++)
                if (transforms[i].name == name) return transforms[i];
            throw new InvalidOperationException($"Could not find TestGI marker '{name}'.");
        }

        private static Light FindDirectionalSun(Scene scene)
        {
            Light[] lights = FindComponents<Light>(scene);
            for (int i = 0; i < lights.Length; i++)
                if (lights[i].type == LightType.Directional) return lights[i];
            throw new InvalidOperationException("TestGI has no directional Sun.");
        }

        private static void DestroyGeneratedRoot(Scene scene, string name)
        {
            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
                if (roots[i].name == name) UnityEngine.Object.DestroyImmediate(roots[i]);
        }
    }
}
