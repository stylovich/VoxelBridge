using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
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

        private static void DestroyGeneratedRoot(Scene scene, string name)
        {
            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
                if (roots[i].name == name) UnityEngine.Object.DestroyImmediate(roots[i]);
        }
    }
}
