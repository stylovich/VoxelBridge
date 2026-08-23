using System;
using System.Collections.Generic;
using DynamicGI.Contributors;
using DynamicGI.Debugging;
using DynamicGI.Geometry;
using DynamicGI.Occlusion;
using DynamicGI.Radiance;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace DynamicGI.Editor
{
    /// <summary>Configures and validates bounded neighbor propagation in TestGI.</summary>
    public static class RadiancePhase8SceneSetup
    {
        private const string ScenePath = "Assets/Scenes/TestGI.unity";
        private const string RootName = "Dynamic GI Phase 8 - Propagation Test";
        private static readonly Color TestEmission = new(1f, 0.025f, 0.65f, 1f);

        [MenuItem("Tools/Dynamic GI/Phase 8/Configure TestGI Diffuse Propagation")]
        public static void ConfigureTestGI()
        {
            RadiancePhase7SceneSetup.ConfigureTestGI();
            Scene scene = SceneManager.GetActiveScene();
            DestroyGeneratedRoot(scene, RootName);

            GameObject root = new(RootName);
            SceneManager.MoveGameObjectToScene(root, scene);
            Transform ceiling = CreateMarker(root.transform, "Ceiling Bounce Probe Marker", new Vector3(2.25f, 4.25f, -3.25f));
            Transform propagationMid = CreateMarker(root.transform, "Propagation Mid Probe Marker", new Vector3(2.25f, 3.25f, -3.25f));
            Transform blocked = FindNamedTransform(scene, "Emissive Blocked Probe Marker");
            Transform occluder = FindNamedTransform(scene, "Emissive DDA Occluder");
            GIEmissiveContributor contributor = FindSingle<GIEmissiveContributor>(scene);

            Phase8PropagationTestGuide guide = root.AddComponent<Phase8PropagationTestGuide>();
            SerializedObject serializedGuide = new(guide);
            Set(serializedGuide, "contributor", contributor);
            Set(serializedGuide, "ceilingBounceMarker", ceiling);
            Set(serializedGuide, "blockedMarker", blocked);
            Set(serializedGuide, "occluder", occluder);
            serializedGuide.ApplyModifiedPropertiesWithoutUndo();

            WorldRadianceClipmap clipmap = FindSingle<WorldRadianceClipmap>(scene);
            SerializedObject serializedClipmap = new(clipmap);
            Set(serializedClipmap, "enableDiffusePropagation", true);
            // C0 spacing is 0.5 m and the room is 5 m tall. Ten passes let a floor
            // reflection reach the underside of the ceiling in this laboratory.
            Set(serializedClipmap, "propagationIterations", 10);
            Set(serializedClipmap, "propagationStrength", 0.8f);
            Set(serializedClipmap, "propagationDirectionalRetention", 0.65f);
            Set(serializedClipmap, "propagationDistanceAttenuation", 0.95f);
            Set(serializedClipmap, "propagationSurfaceReflectivity", 0.35f);
            Set(serializedClipmap, "maximumPropagatedRadiance", 8f);
            Set(serializedClipmap, "maximumPropagationCascadeIndex", 1);
            Set(serializedClipmap, "propagationShader", AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Assets/DynamicGI/Shaders/RadiancePropagate.compute"));
            serializedClipmap.ApplyModifiedPropertiesWithoutUndo();

            RadianceClipmapDebug fieldDebug = FindSingle<RadianceClipmapDebug>(scene);
            SerializedObject serializedDebug = new(fieldDebug);
            Set(serializedDebug, "probeQueryTarget", propagationMid);
            Set(serializedDebug, "displayedDirection", (int)RadianceDebugDirection.NegativeY);
            Set(serializedDebug, "displayedSource", (int)RadianceDebugSource.PropagationDelta);
            Set(serializedDebug, "automaticExposure", true);
            Set(serializedDebug, "automaticExposurePercentile", 0.25f);
            Set(serializedDebug, "automaticExposureTarget", 0.65f);
            Set(serializedDebug, "maximumAutomaticExposure", 4096f);
            Set(serializedDebug, "automaticExposureAdaptation", 0.35f);
            Set(serializedDebug, "smallValueDecimalPlaces", 6);
            serializedDebug.ApplyModifiedPropertiesWithoutUndo();

            EditorUtility.SetDirty(root);
            EditorUtility.SetDirty(contributor);
            EditorUtility.SetDirty(clipmap);
            EditorUtility.SetDirty(fieldDebug);
            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene))
                throw new InvalidOperationException($"Could not save {ScenePath}.");

            Debug.Log(
                "DYNAMIC_GI_PHASE8_TESTGI_CONFIGURED | propagation=10x strength0.8 retention0.65 surfaceReflectivity0.35 | " +
                $"C0+C1 | emissiveDirectRange={contributor.InfluenceRange:0.0}m | queryY=3.25 | ceilingY=4.25 | " +
                "debug=PropagationDelta/-Y autoExposure");
        }

        [MenuItem("Tools/Dynamic GI/Phase 8/Validate TestGI Diffuse Propagation")]
        public static void ValidateTestGI()
        {
            Scene scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            WorldGeometryField geometry = FindSingle<WorldGeometryField>(scene);
            WorldSkyVisibilityField sky = FindSingle<WorldSkyVisibilityField>(scene);
            WorldRadianceClipmap clipmap = FindSingle<WorldRadianceClipmap>(scene);
            GIEmissiveContributor contributor = FindSingle<GIEmissiveContributor>(scene);
            Light sun = FindDirectionalSun(scene);
            Transform ceiling = FindNamedTransform(scene, "Ceiling Bounce Probe Marker");
            Transform blocked = FindNamedTransform(scene, "Emissive Blocked Probe Marker");
            Transform direct = FindNamedTransform(scene, "Emissive Visible Probe Marker");

            bool originalSunEnabled = sun.enabled;
            Color originalColor = contributor.EmissionColor;
            float originalIntensity = contributor.EmissionIntensity;
            float originalRange = contributor.InfluenceRange;
            int originalCascade = contributor.MaximumCascadeIndex;
            bool originalContribution = contributor.Contributes;
            float originalPropagationStrength = clipmap.PropagationStrength;

            try
            {
                sun.enabled = false;
                contributor.Configure(
                    ToRendererArray(contributor.Renderers),
                    TestEmission,
                    20f,
                    0.1f,
                    1,
                    true);
                geometry.RebuildAll();
                geometry.ProcessAllDirtyNow();
                sky.RebuildAll();
                sky.ProcessAllDirtyNow();

                clipmap.SetPropagationStrength(0f);
                clipmap.ForceLightingRefresh();
                clipmap.ProcessAllDirtyNow();
                RadianceClipmapProbeResult directReference = Query(clipmap, direct.position);
                Vector4 ceilingDirectOnly = Query(clipmap, ceiling.position).Probe.Radiance.NegativeY;

                clipmap.SetPropagationStrength(0.8f);
                clipmap.ProcessAllDirtyNow();
                Vector4 propagationStep0 = Query(clipmap, new Vector3(2.25f, 2.75f, -3.25f)).Probe.Radiance.NegativeY;
                Vector4 propagationStep1 = Query(clipmap, new Vector3(2.25f, 3.25f, -3.25f)).Probe.Radiance.NegativeY;
                Vector4 propagationStep2 = Query(clipmap, new Vector3(2.25f, 3.75f, -3.25f)).Probe.Radiance.NegativeY;
                Vector4 ceilingPropagated = Query(clipmap, ceiling.position).Probe.Radiance.NegativeY;
                Vector4 debugResolved = QueryDebug(clipmap, ceiling.position, RadianceDebugSource.Resolved).Probe.Radiance.NegativeY;
                Vector4 debugDirect = QueryDebug(clipmap, ceiling.position, RadianceDebugSource.Direct).Probe.Radiance.NegativeY;
                Vector4 debugDelta = QueryDebug(clipmap, ceiling.position, RadianceDebugSource.PropagationDelta).Probe.Radiance.NegativeY;
                Vector4 expectedDebugDelta = MaxZero(debugResolved - debugDirect);
                float debugDeltaError = MaximumRgbError(debugDelta, expectedDebugDelta);
                float debugSliceLuminance = ReadNearestDebugDeltaSample(clipmap, ceiling.position);
                RadianceClipmapProbeResult blockedPropagatedResult = Query(clipmap, blocked.position);

                contributor.SetContributionEnabled(false);
                clipmap.ProcessAllDirtyNow();
                Vector4 ceilingWithoutEmissive = Query(clipmap, ceiling.position).Probe.Radiance.NegativeY;
                RadianceClipmapProbeResult blockedWithoutEmissiveResult = Query(clipmap, blocked.position);
                Vector4 ceilingEmissiveBounce = MaxZero(ceilingPropagated - ceilingWithoutEmissive);
                float ceilingBounce = Luminance(ceilingEmissiveBounce);
                float blockedIncrease = Mathf.Max(
                    0f,
                    blockedPropagatedResult.Probe.AverageLuminance -
                    blockedWithoutEmissiveResult.Probe.AverageLuminance);

                if (directReference.Probe.AverageLuminance <= 0.05f)
                    throw new InvalidOperationException("Phase 8 direct emissive reference has no source energy.");
                if (ceilingBounce <= 0.02f)
                {
                    throw new InvalidOperationException(
                        $"Configured propagation did not produce a useful ceiling rebound: direct={FormatRgb(ceilingDirectOnly)}, " +
                        $"steps={FormatRgb(propagationStep0)} -> {FormatRgb(propagationStep1)} -> " +
                        $"{FormatRgb(propagationStep2)} -> {FormatRgb(ceilingPropagated)}.");
                }
                if (debugDeltaError > 0.0001f ||
                    Mathf.Abs(debugSliceLuminance - Luminance(debugDelta)) > 0.0001f)
                {
                    throw new InvalidOperationException(
                        $"Propagation debug delta disagrees with resolved-direct: delta={FormatRgb(debugDelta)}, " +
                        $"expected={FormatRgb(expectedDebugDelta)}, sliceL={debugSliceLuminance:0.000000}.");
                }
                if (ceilingEmissiveBounce.x < ceilingEmissiveBounce.y * 4f + 0.0002f ||
                    ceilingEmissiveBounce.z < ceilingEmissiveBounce.y * 4f + 0.00005f)
                {
                    throw new InvalidOperationException($"Ceiling bounce did not retain emissive color: {ceilingEmissiveBounce}.");
                }
                if (blockedIncrease > Mathf.Max(0.0005f, ceilingBounce * 0.05f))
                {
                    throw new InvalidOperationException(
                        $"Propagation leaked through the voxel wall: blockedDelta={blockedIncrease:0.0000}, " +
                        $"ceilingDelta={ceilingBounce:0.0000}.");
                }

                RadianceClipmapStats stats = clipmap.Stats;
                if (!stats.PropagationEnabled || stats.PropagationDispatchesThisFrame <= 0 ||
                    stats.PropagatedProbesThisFrame <= 0 || stats.PropagationIterations < 10)
                {
                    throw new InvalidOperationException("Propagation statistics did not record GPU work.");
                }

                Debug.Log(
                    $"DYNAMIC_GI_PHASE8_VALIDATION_PASSED | directRefL={directReference.Probe.AverageLuminance:0.0000} | " +
                    $"steps={FormatRgb(propagationStep0)}>{FormatRgb(propagationStep1)}>{FormatRgb(propagationStep2)} | " +
                    $"ceilingDirect={FormatRgb(ceilingDirectOnly)} | ceilingPropagated={FormatRgb(ceilingPropagated)} | " +
                    $"ceilingNoEmitter={FormatRgb(ceilingWithoutEmissive)} | ceilingDelta={ceilingBounce:0.0000} | " +
                    $"debugDelta={FormatRgb(debugDelta)} | debugSliceL={debugSliceLuminance:0.000000} | " +
                    $"blockedEmitterDelta={blockedIncrease:0.0000} | " +
                    $"iterations={stats.PropagationIterations} | dispatches={stats.PropagationDispatchesThisFrame} | " +
                    $"writes={stats.PropagatedProbesThisFrame} | GPU={EditorUtility.FormatBytes(stats.EstimatedGpuBytes)}");
            }
            finally
            {
                contributor.Configure(
                    ToRendererArray(contributor.Renderers),
                    originalColor,
                    originalIntensity,
                    originalRange,
                    originalCascade,
                    originalContribution);
                sun.enabled = originalSunEnabled;
                clipmap.SetPropagationStrength(originalPropagationStrength);
                if (clipmap.IsInitialized)
                {
                    clipmap.ForceLightingRefresh();
                    clipmap.ProcessAllDirtyNow();
                }
            }
        }

        private static RadianceClipmapProbeResult Query(WorldRadianceClipmap field, Vector3 position)
        {
            RadianceClipmapProbeResult result = default;
            bool complete = false;
            if (!field.RequestProbe(position, value => { result = value; complete = true; }))
                throw new InvalidOperationException("Phase 8 clipmap rejected a GPU query.");
            AsyncGPUReadback.WaitAllRequests();
            if (!complete || result.Probe.HasError)
                throw new InvalidOperationException("Phase 8 GPU radiance query failed.");
            return result;
        }

        private static RadianceClipmapProbeResult QueryDebug(
            WorldRadianceClipmap field,
            Vector3 position,
            RadianceDebugSource source)
        {
            RadianceClipmapProbeResult result = default;
            bool complete = false;
            if (!field.RequestDebugProbe(position, source, value => { result = value; complete = true; }))
                throw new InvalidOperationException($"Phase 8 clipmap rejected a {source} GPU debug query.");
            AsyncGPUReadback.WaitAllRequests();
            if (!complete || result.Probe.HasError)
                throw new InvalidOperationException($"Phase 8 {source} GPU debug query failed.");
            return result;
        }

        private static float ReadNearestDebugDeltaSample(WorldRadianceClipmap field, Vector3 position)
        {
            if (!field.TryGetCascade(0, out RadianceCascade cascade))
                throw new InvalidOperationException("Phase 8 debug validation requires cascade 0.");
            using GraphicsBuffer buffer = new(
                GraphicsBuffer.Target.Structured,
                cascade.ProbeCount,
                RadianceDebugSampleGpu.Stride);
            if (!field.BuildDebugSamples(
                    0,
                    buffer,
                    cascade.ProbeCount,
                    cascade.WorldBounds.center,
                    0f,
                    RadianceDebugDirection.NegativeY,
                    RadianceDebugSource.PropagationDelta,
                    out int count))
            {
                throw new InvalidOperationException("Phase 8 could not build propagation-delta debug samples.");
            }

            RadianceDebugSampleGpu[] samples = new RadianceDebugSampleGpu[count];
            buffer.GetData(samples);
            float nearestDistance = float.PositiveInfinity;
            float nearestLuminance = 0f;
            for (int i = 0; i < samples.Length; i++)
            {
                Vector4 sample = samples[i].PositionAndLuminance;
                Vector3 samplePosition = new(sample.x, sample.y, sample.z);
                float distance = (samplePosition - position).sqrMagnitude;
                if (distance >= nearestDistance)
                    continue;
                nearestDistance = distance;
                nearestLuminance = sample.w;
            }
            if (nearestDistance > cascade.ProbeSpacing * cascade.ProbeSpacing * 0.01f)
                throw new InvalidOperationException("Phase 8 debug sample grid did not contain the ceiling marker.");
            return nearestLuminance;
        }

        private static Transform CreateMarker(Transform parent, string name, Vector3 position)
        {
            GameObject marker = new(name);
            marker.transform.SetParent(parent, true);
            marker.transform.position = position;
            return marker.transform;
        }

        private static void DestroyGeneratedRoot(Scene scene, string name)
        {
            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
                if (roots[i].name == name) UnityEngine.Object.DestroyImmediate(roots[i]);
        }

        private static Light FindDirectionalSun(Scene scene)
        {
            Light[] lights = FindComponents<Light>(scene);
            for (int i = 0; i < lights.Length; i++)
                if (lights[i].type == LightType.Directional) return lights[i];
            throw new InvalidOperationException("TestGI has no directional Sun.");
        }

        private static T FindSingle<T>(Scene scene) where T : Component
        {
            T[] values = FindComponents<T>(scene);
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

        private static Renderer[] ToRendererArray(IReadOnlyList<Renderer> values)
        {
            Renderer[] result = new Renderer[values.Count];
            for (int i = 0; i < result.Length; i++) result[i] = values[i];
            return result;
        }

        private static float Luminance(Vector4 value) => value.x * 0.2126f + value.y * 0.7152f + value.z * 0.0722f;
        private static float MaximumRgbError(Vector4 a, Vector4 b) => Mathf.Max(
            Mathf.Abs(a.x - b.x),
            Mathf.Max(Mathf.Abs(a.y - b.y), Mathf.Abs(a.z - b.z)));
        private static Vector4 MaxZero(Vector4 value) => new(
            Mathf.Max(0f, value.x),
            Mathf.Max(0f, value.y),
            Mathf.Max(0f, value.z),
            Mathf.Max(0f, value.w));
        private static string FormatRgb(Vector4 value) => $"({value.x:0.0000},{value.y:0.0000},{value.z:0.0000})";
        private static void Set(SerializedObject target, string name, bool value) => target.FindProperty(name).boolValue = value;
        private static void Set(SerializedObject target, string name, int value) => target.FindProperty(name).intValue = value;
        private static void Set(SerializedObject target, string name, float value) => target.FindProperty(name).floatValue = value;
        private static void Set(SerializedObject target, string name, UnityEngine.Object value) => target.FindProperty(name).objectReferenceValue = value;
    }
}
