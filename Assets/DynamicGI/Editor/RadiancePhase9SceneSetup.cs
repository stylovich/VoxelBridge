using System;
using System.Collections.Generic;
using DynamicGI.Contributors;
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
    /// <summary>Configures and validates staggered temporal radiance accumulation in TestGI.</summary>
    public static class RadiancePhase9SceneSetup
    {
        private const string ScenePath = "Assets/Scenes/TestGI.unity";
        private static readonly float[] CascadeAlphas = { 0.2f, 0.3f, 0.4f };
        private static readonly int[] CascadeSteps = { 16, 12, 8 };
        private static readonly Color TestEmission = new(1f, 0.025f, 0.65f, 1f);

        [MenuItem("Tools/Dynamic GI/Phase 9/Configure TestGI Temporal Updates")]
        public static void ConfigureTestGI()
        {
            RadiancePhase8SceneSetup.ConfigureTestGI();
            Scene scene = SceneManager.GetActiveScene();
            WorldRadianceClipmap clipmap = FindSingle<WorldRadianceClipmap>(scene);
            SerializedObject serializedClipmap = new(clipmap);
            Set(serializedClipmap, "enableTemporalAccumulation", true);
            Set(serializedClipmap, "temporalShader", AssetDatabase.LoadAssetAtPath<ComputeShader>(
                "Assets/DynamicGI/Shaders/RadianceTemporal.compute"));

            SerializedProperty cascades = serializedClipmap.FindProperty("cascadeSettings");
            int configured = Mathf.Min(cascades.arraySize, CascadeAlphas.Length);
            for (int i = 0; i < configured; i++)
            {
                SerializedProperty cascade = cascades.GetArrayElementAtIndex(i);
                cascade.FindPropertyRelative("temporalAlpha").floatValue = CascadeAlphas[i];
                cascade.FindPropertyRelative("temporalConvergenceSteps").intValue = CascadeSteps[i];
            }
            serializedClipmap.ApplyModifiedPropertiesWithoutUndo();

            EditorUtility.SetDirty(clipmap);
            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene))
                throw new InvalidOperationException($"Could not save {ScenePath}.");

            Debug.Log(
                "DYNAMIC_GI_PHASE9_TESTGI_CONFIGURED | temporal=on | " +
                "C0=1f alpha0.20 x16 | C1=2f alpha0.30 x12 | C2=4f alpha0.40 x8 | " +
                "recycled/geometry tiles reset history");
        }

        [MenuItem("Tools/Dynamic GI/Phase 9/Validate TestGI Temporal Updates")]
        public static void ValidateTestGI()
        {
            Scene scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            WorldGeometryField geometry = FindSingle<WorldGeometryField>(scene);
            WorldSkyVisibilityField sky = FindSingle<WorldSkyVisibilityField>(scene);
            WorldRadianceClipmap clipmap = FindSingle<WorldRadianceClipmap>(scene);
            GIEmissiveContributor contributor = FindSingle<GIEmissiveContributor>(scene);
            Light sun = FindDirectionalSun(scene);
            Transform directMarker = FindNamedTransform(scene, "Emissive Visible Probe Marker");

            bool originalSunEnabled = sun.enabled;
            Color originalColor = contributor.EmissionColor;
            float originalIntensity = contributor.EmissionIntensity;
            float originalRange = contributor.InfluenceRange;
            int originalMaximumCascade = contributor.MaximumCascadeIndex;
            bool originalContribution = contributor.Contributes;
            float originalPropagationStrength = clipmap.PropagationStrength;
            List<float> originalAlphas = new();
            List<int> originalSteps = new();

            try
            {
                if (!clipmap.TemporalAccumulationEnabled)
                    throw new InvalidOperationException("Phase 9 temporal compute is not enabled or available.");
                for (int i = 0; i < clipmap.CascadeCount; i++)
                {
                    if (!clipmap.TryGetCascade(i, out RadianceCascade cascade))
                        throw new InvalidOperationException($"Could not inspect temporal cascade {i}.");
                    originalAlphas.Add(cascade.TemporalAlpha);
                    originalSteps.Add(cascade.TemporalConvergenceSteps);
                }

                sun.enabled = false;
                contributor.Configure(
                    ToRendererArray(contributor.Renderers),
                    TestEmission,
                    20f,
                    0.1f,
                    1,
                    true);
                clipmap.SetPropagationStrength(0f);
                clipmap.SetTemporalParameters(0, 0.25f, 4);

                geometry.RebuildAll();
                geometry.ProcessAllDirtyNow();
                sky.RebuildAll();
                sky.ProcessAllDirtyNow();
                clipmap.RebuildAll();
                clipmap.ProcessAllDirtyNow();

                Vector4 baseline = Query(clipmap, directMarker.position).Probe.Radiance.NegativeZ;
                contributor.SetContributionEnabled(false);
                clipmap.ProcessAllCandidateUpdatesNow();
                RadianceClipmapStats firstStats = clipmap.Stats;
                Vector4 firstTemporal = Query(clipmap, directMarker.position).Probe.Radiance.NegativeZ;
                if (firstStats.PendingTemporalTiles <= 0)
                    throw new InvalidOperationException("A smooth emissive change did not queue temporal convergence.");

                clipmap.ProcessAllTemporalNow();
                RadianceClipmapStats convergedStats = clipmap.Stats;
                Vector4 finalValue = Query(clipmap, directMarker.position).Probe.Radiance.NegativeZ;
                Vector4 expectedFirst = Vector4.Lerp(baseline, finalValue, 0.25f);
                float firstError = MaximumRgbError(firstTemporal, expectedFirst);
                float expectedMagnitude = Mathf.Max(
                    Mathf.Abs(expectedFirst.x),
                    Mathf.Max(Mathf.Abs(expectedFirst.y), Mathf.Abs(expectedFirst.z)));
                // Cascades use RGBA16F when supported. Its quantization error grows
                // with magnitude, so validate the lerp against half-float relative
                // precision instead of a fixed threshold calibrated near value one.
                float lerpTolerance = Mathf.Max(0.0025f, expectedMagnitude * 0.001f);
                float transitionMagnitude = MaximumRgbError(baseline, finalValue);

                if (transitionMagnitude <= 0.02f)
                {
                    throw new InvalidOperationException(
                        $"Phase 9 temporal test source did not produce a measurable transition: " +
                        $"before={FormatRgb(baseline)}, after={FormatRgb(finalValue)}.");
                }
                if (firstError > lerpTolerance)
                {
                    throw new InvalidOperationException(
                        $"First temporal result does not match lerp(previous, candidate, 0.25): " +
                        $"before={FormatRgb(baseline)}, first={FormatRgb(firstTemporal)}, " +
                        $"candidate={FormatRgb(finalValue)}, expected={FormatRgb(expectedFirst)}, " +
                        $"error={firstError:0.000000}, tolerance={lerpTolerance:0.000000}.");
                }
                if (convergedStats.PendingTemporalTiles != 0 ||
                    MaximumRgbError(finalValue, firstTemporal) <= 0.005f)
                {
                    throw new InvalidOperationException(
                        $"Temporal queue did not converge to its candidate: pending={convergedStats.PendingTemporalTiles}, " +
                        $"first={FormatRgb(firstTemporal)}, final={FormatRgb(finalValue)}.");
                }

                int resetsBeforeScroll = convergedStats.TemporalResetTilesThisFrame;
                if (!clipmap.TryGetCascade(0, out RadianceCascade cascade0))
                    throw new InvalidOperationException("Phase 9 requires cascade 0.");
                Vector3 trackingPosition = clipmap.TrackingTarget != null
                    ? clipmap.TrackingTarget.position
                    : cascade0.WorldBounds.center;
                cascade0.UpdateOrigin(trackingPosition + Vector3.right * cascade0.TileWorldSize);
                int exposed = cascade0.LastExposedTiles;
                clipmap.ProcessAllCandidateUpdatesNow();
                int scrollResets = clipmap.Stats.TemporalResetTilesThisFrame - resetsBeforeScroll;
                if (exposed <= 0 || scrollResets < exposed)
                {
                    throw new InvalidOperationException(
                        $"New toroidal slab did not replace recycled history: exposed={exposed}, resetTiles={scrollResets}.");
                }

                int[] expectedIntervals = { 1, 2, 4 };
                for (int i = 0; i < Mathf.Min(expectedIntervals.Length, clipmap.CascadeCount); i++)
                {
                    clipmap.TryGetCascade(i, out RadianceCascade cascade);
                    if (cascade.UpdateIntervalFrames != expectedIntervals[i])
                    {
                        throw new InvalidOperationException(
                            $"Cascade {i} cadence is {cascade.UpdateIntervalFrames}, expected {expectedIntervals[i]}.");
                    }
                }

                Debug.Log(
                    $"DYNAMIC_GI_PHASE9_VALIDATION_PASSED | alpha=0.25 | " +
                    $"before={FormatRgb(baseline)} | first={FormatRgb(firstTemporal)} | " +
                    $"final={FormatRgb(finalValue)} | lerpError/tolerance={firstError:0.000000}/{lerpTolerance:0.000000} | " +
                    $"pendingFirst={firstStats.PendingTemporalTiles} | pendingFinal={convergedStats.PendingTemporalTiles} | " +
                    $"temporalDispatches={convergedStats.TemporalDispatchesThisFrame} | " +
                    $"temporalWrites={convergedStats.TemporalProbesThisFrame} | " +
                    $"exposed/resets={exposed}/{scrollResets} | GPU={EditorUtility.FormatBytes(convergedStats.EstimatedGpuBytes)}");
            }
            finally
            {
                contributor.Configure(
                    ToRendererArray(contributor.Renderers),
                    originalColor,
                    originalIntensity,
                    originalRange,
                    originalMaximumCascade,
                    originalContribution);
                sun.enabled = originalSunEnabled;
                clipmap.SetPropagationStrength(originalPropagationStrength);
                for (int i = 0; i < Mathf.Min(clipmap.CascadeCount, originalAlphas.Count); i++)
                    clipmap.SetTemporalParameters(i, originalAlphas[i], originalSteps[i]);
                if (clipmap.TryGetCascade(0, out RadianceCascade cascade0))
                {
                    Vector3 target = clipmap.TrackingTarget != null
                        ? clipmap.TrackingTarget.position
                        : clipmap.transform.position;
                    cascade0.UpdateOrigin(target);
                }
                if (clipmap.IsInitialized)
                {
                    clipmap.RebuildAll();
                    clipmap.ProcessAllDirtyNow();
                }
            }
        }

        private static RadianceClipmapProbeResult Query(WorldRadianceClipmap field, Vector3 position)
        {
            RadianceClipmapProbeResult result = default;
            bool complete = false;
            if (!field.RequestProbe(position, value => { result = value; complete = true; }))
                throw new InvalidOperationException("Phase 9 clipmap rejected a GPU query.");
            AsyncGPUReadback.WaitAllRequests();
            if (!complete || result.Probe.HasError)
                throw new InvalidOperationException("Phase 9 GPU radiance query failed.");
            return result;
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

        private static float MaximumRgbError(Vector4 a, Vector4 b) => Mathf.Max(
            Mathf.Abs(a.x - b.x),
            Mathf.Max(Mathf.Abs(a.y - b.y), Mathf.Abs(a.z - b.z)));
        private static string FormatRgb(Vector4 value) => $"({value.x:0.0000},{value.y:0.0000},{value.z:0.0000})";
        private static void Set(SerializedObject target, string name, bool value) =>
            target.FindProperty(name).boolValue = value;
        private static void Set(SerializedObject target, string name, UnityEngine.Object value) =>
            target.FindProperty(name).objectReferenceValue = value;
    }
}
