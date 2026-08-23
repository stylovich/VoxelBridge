using System;
using System.Collections.Generic;
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
    /// <summary>Configures and validates realtime Sun injection in the TestGI room.</summary>
    public static class RadiancePhase6SceneSetup
    {
        private const string ScenePath = "Assets/Scenes/TestGI.unity";

        [MenuItem("Tools/Dynamic GI/Phase 6/Configure TestGI Sun Injection")]
        public static void ConfigureTestGI()
        {
            RadiancePhase5SceneSetup.ConfigureTestGI();
            Debug.Log(
                "DYNAMIC_GI_PHASE6_TESTGI_CONFIGURED | automatic sun transform/color/intensity detection | " +
                "horizon fade=3deg | numeric slices=query+ground(0.25m)+ceiling(4.25m)");
        }

        [MenuItem("Tools/Dynamic GI/Phase 6/Validate TestGI Sun Cycle")]
        public static void ValidateTestGI()
        {
            Scene scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            WorldGeometryField geometry = FindSingle<WorldGeometryField>(scene);
            WorldSkyVisibilityField sky = FindSingle<WorldSkyVisibilityField>(scene);
            WorldRadianceClipmap clipmap = FindSingle<WorldRadianceClipmap>(scene);
            RadianceClipmapDebug fieldDebug = FindSingle<RadianceClipmapDebug>(scene);
            Light sun = FindDirectionalSun(scene);
            Transform lit = FindNamedTransform(scene, "Lit Probe Marker");
            Transform blocked = FindNamedTransform(scene, "Blocked Probe Marker");

            Quaternion originalRotation = sun.transform.rotation;
            Color originalColor = sun.color;
            float originalIntensity = sun.intensity;
            bool originalEnabled = sun.enabled;

            try
            {
                if (!clipmap.TryGetCascade(0, out RadianceCascade cascade0) || cascade0.Resolution.y < 16)
                    throw new InvalidOperationException("Configure Phase 6 first: C0 must cover floor and ceiling slices.");

                geometry.RebuildAll();
                geometry.ProcessAllDirtyNow();
                sky.RebuildAll();
                sky.ProcessAllDirtyNow();

                sun.enabled = true;
                sun.color = new Color(1f, 0.9f, 0.75f);
                sun.intensity = 130000f;
                SetSunRayDirection(sun, new Vector3(-1f, -0.15f, 0f));
                clipmap.ForceLightingRefresh();
                clipmap.ProcessAllDirtyNow();

                RadianceClipmapProbeResult east = Query(clipmap, lit.position);
                RadianceClipmapProbeResult eastBlocked = Query(clipmap, blocked.position);
                float eastPositiveX = Luminance(east.Probe.Radiance.PositiveX);
                float blockedPositiveX = Luminance(eastBlocked.Probe.Radiance.PositiveX);
                if (eastPositiveX < blockedPositiveX + 0.35f)
                    throw new InvalidOperationException("East Sun did not preserve window/wall separation.");

                int revision = clipmap.SunRevision;
                sun.intensity = 65000f;
                RadianceClipmapStats scheduled = DetectAndValidateScheduledRefresh(clipmap, revision, "intensity");
                clipmap.ProcessAllDirtyNow();
                float halfIntensity = Luminance(Query(clipmap, lit.position).Probe.Radiance.PositiveX);
                if (halfIntensity > eastPositiveX - 0.25f)
                    throw new InvalidOperationException($"Sun intensity change was not injected: {eastPositiveX:0.000} -> {halfIntensity:0.000}.");

                revision = clipmap.SunRevision;
                sun.intensity = 130000f;
                sun.color = new Color(0.1f, 0.3f, 1f);
                DetectAndValidateScheduledRefresh(clipmap, revision, "color");
                clipmap.ProcessAllDirtyNow();
                Vector4 blueRadiance = Query(clipmap, lit.position).Probe.Radiance.PositiveX;
                if (blueRadiance.z < blueRadiance.x + 0.35f)
                    throw new InvalidOperationException($"Sun color change was not injected: {blueRadiance}.");

                revision = clipmap.SunRevision;
                sun.color = originalColor;
                SetSunRayDirection(sun, new Vector3(1f, -0.15f, 0f));
                DetectAndValidateScheduledRefresh(clipmap, revision, "direction east-to-west");
                clipmap.ProcessAllDirtyNow();
                RadianceProbeGpuData west = Query(clipmap, lit.position).Probe.Radiance;
                float westPositiveX = Luminance(west.PositiveX);
                if (westPositiveX > eastPositiveX - 0.35f)
                    throw new InvalidOperationException("West Sun did not remove direct +X irradiance from the east window path.");

                revision = clipmap.SunRevision;
                SetSunRayDirection(sun, new Vector3(-1f, 0.15f, 0f));
                DetectAndValidateScheduledRefresh(clipmap, revision, "below-horizon night");
                clipmap.ProcessAllDirtyNow();
                RadianceProbeGpuData night = Query(clipmap, lit.position).Probe.Radiance;
                float nightPositiveX = Luminance(night.PositiveX);
                if (clipmap.CurrentSunHorizonFactor > 0.001f || nightPositiveX > eastPositiveX - 0.35f)
                    throw new InvalidOperationException("Below-horizon Sun still contributes direct radiance.");

                fieldDebug.SendMessage("LateUpdate", SendMessageOptions.RequireReceiver);
                AsyncGPUReadback.WaitAllRequests();
                if (fieldDebug.QuerySliceSampleCount <= 0 ||
                    fieldDebug.GroundSliceSampleCount <= 0 ||
                    fieldDebug.CeilingSliceSampleCount <= 0)
                {
                    throw new InvalidOperationException(
                        $"Numeric slices are incomplete: query={fieldDebug.QuerySliceSampleCount}, " +
                        $"ground={fieldDebug.GroundSliceSampleCount}, ceiling={fieldDebug.CeilingSliceSampleCount}.");
                }

                RadianceClipmapStats finalStats = clipmap.Stats;
                Debug.Log(
                    $"DYNAMIC_GI_PHASE6_VALIDATION_PASSED | east+X={eastPositiveX:0.000} | " +
                    $"half+X={halfIntensity:0.000} | blueRGB=({blueRadiance.x:0.000},{blueRadiance.y:0.000},{blueRadiance.z:0.000}) | " +
                    $"west+X={westPositiveX:0.000} | night+X={nightPositiveX:0.000} | " +
                    $"scheduled={scheduled.UpdatedProbesThisFrame}/{scheduled.ActiveProbes} probes | " +
                    $"slices={fieldDebug.QuerySliceSampleCount}/{fieldDebug.GroundSliceSampleCount}/{fieldDebug.CeilingSliceSampleCount} | " +
                    $"sunRevision={finalStats.SunRevision} | GPU={EditorUtility.FormatBytes(finalStats.EstimatedGpuBytes)}");
            }
            finally
            {
                sun.transform.rotation = originalRotation;
                sun.color = originalColor;
                sun.intensity = originalIntensity;
                sun.enabled = originalEnabled;
                if (clipmap != null && clipmap.IsInitialized)
                {
                    clipmap.ForceLightingRefresh();
                    clipmap.ProcessAllDirtyNow();
                }
            }
        }

        private static RadianceClipmapStats DetectAndValidateScheduledRefresh(
            WorldRadianceClipmap clipmap,
            int previousRevision,
            string change)
        {
            clipmap.SendMessage("Update", SendMessageOptions.RequireReceiver);
            RadianceClipmapStats stats = clipmap.Stats;
            if (clipmap.SunRevision <= previousRevision || stats.LightingRefreshesThisFrame != 1)
                throw new InvalidOperationException($"Automatic Sun {change} detection did not advance its revision.");
            if (stats.UpdatedProbesThisFrame <= 0 || stats.UpdatedProbesThisFrame >= stats.ActiveProbes || stats.DirtyTiles <= 0)
            {
                throw new InvalidOperationException(
                    $"Sun {change} refresh was not temporally budgeted: updated={stats.UpdatedProbesThisFrame}, " +
                    $"active={stats.ActiveProbes}, dirty={stats.DirtyTiles}.");
            }
            return stats;
        }

        private static void SetSunRayDirection(Light sun, Vector3 rayDirection) =>
            sun.transform.rotation = Quaternion.LookRotation(rayDirection.normalized, Vector3.up);

        private static RadianceClipmapProbeResult Query(WorldRadianceClipmap field, Vector3 position)
        {
            RadianceClipmapProbeResult result = default;
            bool complete = false;
            if (!field.RequestProbe(position, value => { result = value; complete = true; }))
                throw new InvalidOperationException("Phase 6 clipmap rejected a GPU query.");
            AsyncGPUReadback.WaitAllRequests();
            if (!complete || result.Probe.HasError)
                throw new InvalidOperationException("Phase 6 GPU radiance query failed.");
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

        private static float Luminance(Vector4 value) => value.x * 0.2126f + value.y * 0.7152f + value.z * 0.0722f;
    }
}
