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
    /// <summary>Upgrades the portable TestGI east-window room to the Phase 5 clipmap.</summary>
    public static class RadiancePhase5SceneSetup
    {
        private const string ScenePath = "Assets/Scenes/TestGI.unity";

        [MenuItem("Tools/Dynamic GI/Phase 5/Configure TestGI Radiance Clipmap")]
        public static void ConfigureTestGI()
        {
            // The Phase-4 setup owns the portable room geometry and markers. Reusing it
            // keeps this upgrade idempotent and avoids serializing test meshes in Git.
            RadiancePhase4SceneSetup.ConfigureTestGI();
            Scene scene = SceneManager.GetActiveScene();
            WorldGeometryField geometry = FindSingle<WorldGeometryField>(scene);
            WorldSkyVisibilityField sky = FindSingle<WorldSkyVisibilityField>(scene);
            WorldRadianceField localField = FindSingle<WorldRadianceField>(scene);
            RadianceFieldDebug localDebug = FindSingle<RadianceFieldDebug>(scene);
            Camera camera = FindMainCamera(scene);
            Light sun = FindDirectionalSun(scene);
            Transform litMarker = FindNamedTransform(scene, "Lit Probe Marker");

            GameObject fieldObject = geometry.gameObject;
            fieldObject.SetActive(false);
            localField.enabled = false;
            localDebug.enabled = false;

            WorldRadianceClipmap clipmap = fieldObject.GetComponent<WorldRadianceClipmap>();
            if (clipmap == null) clipmap = fieldObject.AddComponent<WorldRadianceClipmap>();
            RadianceClipmapDebug fieldDebug = fieldObject.GetComponent<RadianceClipmapDebug>();
            if (fieldDebug == null) fieldDebug = fieldObject.AddComponent<RadianceClipmapDebug>();

            camera.usePhysicalProperties = false;
            camera.fieldOfView = 62f;
            camera.transform.position = new Vector3(1.5f, 2.4f, 0f);
            camera.transform.rotation = Quaternion.LookRotation(new Vector3(4.8f, 2.45f, 0f) - camera.transform.position, Vector3.up);
            ConfigureClipmap(clipmap, geometry, sky, sun, camera.transform);
            ConfigureDebug(fieldDebug, clipmap, camera.transform, litMarker);
            fieldObject.SetActive(true);

            EditorUtility.SetDirty(fieldObject);
            EditorUtility.SetDirty(camera);
            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene))
                throw new InvalidOperationException($"Could not save {ScenePath}.");

            Debug.Log(
                "DYNAMIC_GI_PHASE5_TESTGI_CONFIGURED | tracking=Main Camera | " +
                "C0=16x16x16@0.5m | C1=16x8x16@1m | C2=16x8x16@2m | " +
                "tile=2 probes | debug=C0/+X/query+ground+ceiling slices");
        }

        [MenuItem("Tools/Dynamic GI/Phase 5/Validate TestGI Radiance Clipmap")]
        public static void ValidateTestGI()
        {
            Scene scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            WorldGeometryField geometry = FindSingle<WorldGeometryField>(scene);
            WorldSkyVisibilityField sky = FindSingle<WorldSkyVisibilityField>(scene);
            WorldRadianceClipmap clipmap = FindSingle<WorldRadianceClipmap>(scene);
            RadianceClipmapDebug fieldDebug = FindSingle<RadianceClipmapDebug>(scene);
            Transform lit = FindNamedTransform(scene, "Lit Probe Marker");
            Transform blocked = FindNamedTransform(scene, "Blocked Probe Marker");

            geometry.RebuildAll();
            geometry.ProcessAllDirtyNow();
            sky.RebuildAll();
            sky.ProcessAllDirtyNow();
            clipmap.RebuildAll();
            clipmap.ProcessAllDirtyNow();

            RadianceClipmapProbeResult litResult = Query(clipmap, lit.position);
            RadianceClipmapProbeResult blockedResult = Query(clipmap, blocked.position);
            float litValue = Luminance(litResult.Probe.Radiance.PositiveX);
            float blockedValue = Luminance(blockedResult.Probe.Radiance.PositiveX);
            if (litResult.CascadeIndex != 0 || blockedResult.CascadeIndex != 0)
                throw new InvalidOperationException("TestGI markers are not covered by Cascade 0.");
            if (litValue < blockedValue + 0.35f)
            {
                throw new InvalidOperationException(
                    $"TestGI clipmap wall/window separation failed: {litValue:0.000} vs {blockedValue:0.000}.");
            }

            fieldDebug.SendMessage("LateUpdate", SendMessageOptions.RequireReceiver);
            AsyncGPUReadback.WaitAllRequests();
            if (fieldDebug.NumericSampleCount <= 0)
                throw new InvalidOperationException("TestGI clipmap numeric labels received no GPU values.");

            RadianceClipmapStats stats = clipmap.Stats;
            Debug.Log(
                $"DYNAMIC_GI_PHASE5_TESTGI_VALIDATION_PASSED | lit+X={litValue:0.000} | " +
                $"blocked+X={blockedValue:0.000} | cascades={stats.CascadeCount} | " +
                $"probes={stats.ActiveProbes} | labels={fieldDebug.NumericSampleCount} | " +
                $"GPU={EditorUtility.FormatBytes(stats.EstimatedGpuBytes)}");
        }

        private static void ConfigureClipmap(
            WorldRadianceClipmap clipmap,
            WorldGeometryField geometry,
            WorldSkyVisibilityField sky,
            Light sun,
            Transform target)
        {
            SerializedObject value = new(clipmap);
            Set(value, "geometryField", geometry);
            Set(value, "skyVisibilityField", sky);
            Set(value, "sunLight", sun);
            Set(value, "trackingTarget", target);
            Set(value, "cascadeBlendStart", 0.7f);
            Set(value, "skyColor", new Color(0.2f, 0.35f, 0.65f));
            Set(value, "skyIntensity", 0.05f);
            Set(value, "sunIntensityScale", 0.00001f);
            Set(value, "sunTraceDistance", 32f);
            Set(value, "rayOriginBias", 0.05f);
            Set(value, "minimumSunAngularChangeDegrees", 0.1f);
            Set(value, "minimumSunRadianceChange", 0.002f);
            Set(value, "minimumSkyRadianceChange", 0.002f);
            Set(value, "fadeSunBelowHorizon", true);
            Set(value, "sunHorizonFadeDegrees", 3f);
            Set(value, "radianceShader", AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/DynamicGI/Shaders/RadianceInject.compute"));

            SerializedProperty settings = value.FindProperty("cascadeSettings");
            settings.arraySize = 3;
            ConfigureCascade(settings.GetArrayElementAtIndex(0), "Near", 16, 16, 0.5f, 2, 1, 16);
            ConfigureCascade(settings.GetArrayElementAtIndex(1), "Middle", 16, 8, 1f, 2, 2, 8);
            ConfigureCascade(settings.GetArrayElementAtIndex(2), "Far", 16, 8, 2f, 2, 4, 4);
            value.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void ConfigureCascade(
            SerializedProperty value,
            string name,
            int horizontal,
            int vertical,
            float spacing,
            int tile,
            int interval,
            int budget)
        {
            value.FindPropertyRelative("name").stringValue = name;
            value.FindPropertyRelative("enabled").boolValue = true;
            value.FindPropertyRelative("horizontalResolution").intValue = horizontal;
            value.FindPropertyRelative("verticalResolution").intValue = vertical;
            value.FindPropertyRelative("probeSpacing").floatValue = spacing;
            value.FindPropertyRelative("tileResolution").intValue = tile;
            value.FindPropertyRelative("updateIntervalFrames").intValue = interval;
            value.FindPropertyRelative("updateBudgetTiles").intValue = budget;
        }

        private static void ConfigureDebug(
            RadianceClipmapDebug fieldDebug,
            WorldRadianceClipmap clipmap,
            Transform center,
            Transform queryTarget)
        {
            SerializedObject value = new(fieldDebug);
            Set(value, "radianceClipmap", clipmap);
            Set(value, "debugCenterTransform", center);
            Set(value, "probeQueryTarget", queryTarget);
            Set(value, "selectedCascade", 0);
            Set(value, "showAllCascadeBounds", true);
            Set(value, "showDirtyTiles", true);
            Set(value, "showRecentlyUpdatedTiles", true);
            Set(value, "showRingOffsetsAndStats", true);
            Set(value, "showTrackingTarget", true);
            Set(value, "showRadianceProbes", true);
            Set(value, "displayedDirection", (int)RadianceDebugDirection.PositiveX);
            Set(value, "debugRadius", 0f);
            Set(value, "maximumProbeInstances", 8192);
            Set(value, "probeScale", 0.22f);
            Set(value, "exposure", 1.25f);
            Set(value, "showNumericValues", true);
            Set(value, "showQueryNumericSlice", true);
            Set(value, "showGroundNumericSlice", true);
            Set(value, "groundSliceWorldY", 0.25f);
            Set(value, "showCeilingNumericSlice", true);
            Set(value, "ceilingSliceWorldY", 4.25f);
            Set(value, "showNumericSlicePlanes", true);
            Set(value, "maximumNumericLabels", 768);
            Set(value, "numericHorizontalSliceOnly", true);
            Set(value, "queryDetailedProbe", true);
            value.ApplyModifiedPropertiesWithoutUndo();
        }

        private static RadianceClipmapProbeResult Query(WorldRadianceClipmap field, Vector3 position)
        {
            RadianceClipmapProbeResult result = default;
            bool complete = false;
            if (!field.RequestProbe(position, value => { result = value; complete = true; }))
                throw new InvalidOperationException("TestGI Radiance Clipmap rejected a GPU query.");
            AsyncGPUReadback.WaitAllRequests();
            if (!complete || result.Probe.HasError)
                throw new InvalidOperationException("TestGI clipmap radiance query failed.");
            return result;
        }

        private static Camera FindMainCamera(Scene scene)
        {
            Camera[] cameras = FindComponents<Camera>(scene);
            for (int i = 0; i < cameras.Length; i++)
                if (cameras[i].CompareTag("MainCamera")) return cameras[i];
            if (cameras.Length > 0) return cameras[0];
            throw new InvalidOperationException("TestGI has no camera.");
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
        private static void Set(SerializedObject target, string name, float value) => target.FindProperty(name).floatValue = value;
        private static void Set(SerializedObject target, string name, int value) => target.FindProperty(name).intValue = value;
        private static void Set(SerializedObject target, string name, bool value) => target.FindProperty(name).boolValue = value;
        private static void Set(SerializedObject target, string name, Color value) => target.FindProperty(name).colorValue = value;
        private static void Set(SerializedObject target, string name, UnityEngine.Object value) => target.FindProperty(name).objectReferenceValue = value;
    }
}
