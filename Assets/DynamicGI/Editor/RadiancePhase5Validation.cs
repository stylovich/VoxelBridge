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
    /// <summary>GPU validation for snapping, toroidal recycling, and cascade blending.</summary>
    public static class RadiancePhase5Validation
    {
        private const int ValidationLayer = 31;

        [MenuItem("Tools/Dynamic GI/Run Phase 5 Clipmap Validation")]
        public static void Run()
        {
            Scene previousScene = SceneManager.GetActiveScene();
            NewSceneMode mode = Application.isBatchMode ? NewSceneMode.Single : NewSceneMode.Additive;
            Scene testScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, mode);
            SceneManager.SetActiveScene(testScene);
            List<GameObject> objects = new();

            try
            {
                GameObject target = new("Clipmap Tracking Target");
                objects.Add(target);
                Light sun = CreateSun(objects);
                GameObject fieldObject = new("Phase 5 Validation Fields") { layer = ValidationLayer };
                objects.Add(fieldObject);
                fieldObject.SetActive(false);
                WorldGeometryField geometry = fieldObject.AddComponent<WorldGeometryField>();
                WorldSkyVisibilityField sky = fieldObject.AddComponent<WorldSkyVisibilityField>();
                WorldRadianceClipmap clipmap = fieldObject.AddComponent<WorldRadianceClipmap>();
                RadianceClipmapDebug fieldDebug = fieldObject.AddComponent<RadianceClipmapDebug>();
                ConfigureGeometry(geometry);
                ConfigureSky(sky, geometry);
                ConfigureClipmap(clipmap, geometry, sky, sun, target.transform);
                ConfigureDebug(fieldDebug, clipmap, target.transform);
                fieldObject.SetActive(true);

                geometry.RebuildAll();
                geometry.ProcessAllDirtyNow();
                sky.RebuildAll();
                sky.ProcessAllDirtyNow();
                clipmap.RebuildAll();
                clipmap.ProcessAllDirtyNow();
                AssertAllClean(clipmap);

                RadianceCascade cascade0 = GetCascade(clipmap, 0);
                RadianceCascade cascade1 = GetCascade(clipmap, 1);
                Vector3 origin0 = cascade0.OriginWS;
                Vector3 origin1 = cascade1.OriginWS;

                // Sub-tile movement must not move any cascade or invalidate probes.
                target.transform.position = new Vector3(0.4f, 0f, 0f);
                clipmap.SendMessage("Update", SendMessageOptions.RequireReceiver);
                if (cascade0.OriginWS != origin0 || cascade1.OriginWS != origin1)
                    throw new InvalidOperationException("Sub-tile target movement changed a snapped cascade origin.");

                // One metre crosses exactly one Cascade-0 tile (2 probes x 0.5 m),
                // while Cascade 1 remains snapped to its 2 m tile.
                target.transform.position = new Vector3(1.1f, 0f, 0f);
                clipmap.SendMessage("Update", SendMessageOptions.RequireReceiver);
                if (cascade0.OriginWS.x <= origin0.x)
                    throw new InvalidOperationException("Cascade 0 did not follow a crossed tile boundary.");
                if (cascade1.OriginWS != origin1)
                    throw new InvalidOperationException("Cascade 1 moved before its coarser tile boundary.");
                if (cascade0.RingOffset.x != cascade0.TileResolution)
                    throw new InvalidOperationException($"Unexpected Cascade-0 ring offset {cascade0.RingOffset}.");
                if (cascade0.LastExposedProbes != 256 || cascade0.LastRecycledProbes != 1792)
                {
                    throw new InvalidOperationException(
                        $"Toroidal recycle mismatch: exposed={cascade0.LastExposedProbes}, recycled={cascade0.LastRecycledProbes}.");
                }
                if (cascade0.DirtyTileCount <= 0 || cascade0.DirtyTileCount >= cascade0.TotalTileCount)
                    throw new InvalidOperationException("A one-tile scroll did not produce a local entering slab.");

                clipmap.ProcessAllDirtyNow();
                RadianceClipmapProbeResult probe = QuerySynchronously(clipmap, target.transform.position);
                if (probe.CascadeIndex != 0 || probe.Probe.AverageLuminance <= 0f)
                    throw new InvalidOperationException("Finest cascade query returned no injected radiance.");

                Vector4[] sampled = QueryShaderSampling(clipmap, target.transform.position);
                ValidateFinitePositive(sampled[0], "clipmap center");
                ValidateFinitePositive(sampled[1], "cascade blend edge");

                int debugSamples = BuildAndReadDebug(clipmap, target.transform.position);
                if (debugSamples <= 0)
                    throw new InvalidOperationException("Clipmap debug produced no valid probes.");
                fieldDebug.SendMessage("LateUpdate", SendMessageOptions.RequireReceiver);
                AsyncGPUReadback.WaitAllRequests();
                if (fieldDebug.NumericSampleCount <= 0)
                    throw new InvalidOperationException("Clipmap numeric readback produced no samples.");

                // A large teleport cannot reuse the previous window and must invalidate
                // the complete level instead of exposing a misleading partial slab.
                target.transform.position = new Vector3(100f, 0f, 0f);
                clipmap.SendMessage("Update", SendMessageOptions.RequireReceiver);
                if (cascade0.LastExposedProbes != cascade0.ProbeCount || cascade0.LastRecycledProbes != 0)
                    throw new InvalidOperationException("Large teleport did not fully invalidate Cascade 0.");

                RadianceClipmapStats stats = clipmap.Stats;
                Debug.Log(
                    $"DYNAMIC_GI_PHASE5_VALIDATION_PASSED | cascades={stats.CascadeCount} | probes={stats.ActiveProbes} | " +
                    $"C0exposed=256 | C0recycled=1792 | ring={cascade0.RingOffset} | " +
                    $"queryL={probe.Probe.AverageLuminance:0.000} | blendL={Luminance(sampled[1]):0.000} | " +
                    $"debug={debugSamples} | labels={fieldDebug.NumericSampleCount} | GPU={EditorUtility.FormatBytes(stats.EstimatedGpuBytes)}");
            }
            finally
            {
                for (int i = objects.Count - 1; i >= 0; i--)
                    if (objects[i] != null) UnityEngine.Object.DestroyImmediate(objects[i]);
                if (!Application.isBatchMode && testScene.IsValid() && testScene.isLoaded)
                    EditorSceneManager.CloseScene(testScene, true);
                if (previousScene.IsValid() && previousScene.isLoaded)
                    SceneManager.SetActiveScene(previousScene);
            }
        }

        private static Light CreateSun(List<GameObject> objects)
        {
            GameObject value = new("Validation Sun");
            objects.Add(value);
            Light sun = value.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.color = Color.white;
            sun.intensity = 100000f;
            value.transform.rotation = Quaternion.LookRotation(new Vector3(-1f, -1f, 0f).normalized, Vector3.up);
            return sun;
        }

        private static void ConfigureGeometry(WorldGeometryField field)
        {
            SerializedObject value = new(field);
            Set(value, "fieldCenter", Vector3.zero);
            Set(value, "fieldSize", new Vector3(64f, 32f, 64f));
            Set(value, "voxelSize", 0.5f);
            Set(value, "brickResolution", 8);
            Set(value, "maxActiveBricks", 128);
            Set(value, "collectionMode", (int)GeometryCollectionMode.LayerMaskOnly);
            Set(value, "geometryLayers", 1 << ValidationLayer);
            Set(value, "rebuildBudgetBricksPerFrame", 64);
            Set(value, "voxelizationShader", AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/DynamicGI/Shaders/GeometryVoxelize.compute"));
            value.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void ConfigureSky(WorldSkyVisibilityField field, WorldGeometryField geometry)
        {
            SerializedObject value = new(field);
            Set(value, "geometryField", geometry);
            Set(value, "sampleSpacing", 2f);
            Set(value, "rayCount", 8);
            Set(value, "maxTraceDistance", 16f);
            Set(value, "tileResolution", 4);
            Set(value, "updateBudgetTilesPerFrame", 128);
            Set(value, "skyVisibilityShader", AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/DynamicGI/Shaders/SkyVisibility.compute"));
            value.ApplyModifiedPropertiesWithoutUndo();
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
            Set(value, "cascadeBlendStart", 0.65f);
            Set(value, "skyColor", new Color(0.2f, 0.35f, 0.65f));
            Set(value, "skyIntensity", 0.15f);
            Set(value, "sunIntensityScale", 0.00001f);
            Set(value, "sunTraceDistance", 48f);
            Set(value, "radianceShader", AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/DynamicGI/Shaders/RadianceInject.compute"));
            SerializedProperty settings = value.FindProperty("cascadeSettings");
            settings.arraySize = 3;
            ConfigureCascade(settings.GetArrayElementAtIndex(0), "Cascade 0", 16, 8, 0.5f, 2, 1, 16);
            ConfigureCascade(settings.GetArrayElementAtIndex(1), "Cascade 1", 16, 8, 1f, 2, 2, 8);
            ConfigureCascade(settings.GetArrayElementAtIndex(2), "Cascade 2", 16, 8, 2f, 2, 4, 4);
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

        private static void ConfigureDebug(RadianceClipmapDebug fieldDebug, WorldRadianceClipmap clipmap, Transform target)
        {
            SerializedObject value = new(fieldDebug);
            Set(value, "radianceClipmap", clipmap);
            Set(value, "debugCenterTransform", target);
            Set(value, "probeQueryTarget", target);
            Set(value, "selectedCascade", 0);
            Set(value, "showRadianceProbes", true);
            Set(value, "debugRadius", 0f);
            Set(value, "showNumericValues", true);
            Set(value, "maximumNumericLabels", 512);
            value.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void AssertAllClean(WorldRadianceClipmap clipmap)
        {
            for (int i = 0; i < clipmap.CascadeCount; i++)
                if (GetCascade(clipmap, i).DirtyTileCount != 0)
                    throw new InvalidOperationException($"Cascade {i} is still dirty after synchronous processing.");
        }

        private static RadianceCascade GetCascade(WorldRadianceClipmap clipmap, int index)
        {
            if (!clipmap.TryGetCascade(index, out RadianceCascade value))
                throw new InvalidOperationException($"Missing validation cascade {index}.");
            return value;
        }

        private static RadianceClipmapProbeResult QuerySynchronously(WorldRadianceClipmap field, Vector3 position)
        {
            RadianceClipmapProbeResult result = default;
            bool complete = false;
            if (!field.RequestProbe(position, value => { result = value; complete = true; }))
                throw new InvalidOperationException("Clipmap rejected a GPU query.");
            AsyncGPUReadback.WaitAllRequests();
            if (!complete || result.Probe.HasError)
                throw new InvalidOperationException("Clipmap GPU query failed.");
            return result;
        }

        private static Vector4[] QueryShaderSampling(WorldRadianceClipmap clipmap, Vector3 center)
        {
            RadianceCascade cascade0 = GetCascade(clipmap, 0);
            Vector3 edge = cascade0.WorldBounds.center;
            edge.x = cascade0.WorldBounds.max.x - cascade0.ProbeSpacing * 0.75f;
            Vector3[] positions = { center, edge };
            Vector3[] normals = { Vector3.up, Vector3.up };
            Vector4[] results = new Vector4[2];
            ComputeShader shader = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/DynamicGI/Shaders/RadianceSamplingValidation.compute");
            int kernel = shader.FindKernel("QueryDynamicGI");
            if (!clipmap.BindSamplingResources(shader, kernel))
                throw new InvalidOperationException("Could not bind clipmap sampling resources.");
            using GraphicsBuffer positionBuffer = new(GraphicsBuffer.Target.Structured, 2, sizeof(float) * 3);
            using GraphicsBuffer normalBuffer = new(GraphicsBuffer.Target.Structured, 2, sizeof(float) * 3);
            using GraphicsBuffer resultBuffer = new(GraphicsBuffer.Target.Structured, 2, sizeof(float) * 4);
            positionBuffer.SetData(positions);
            normalBuffer.SetData(normals);
            shader.SetBuffer(kernel, "_DynamicGISamplePositions", positionBuffer);
            shader.SetBuffer(kernel, "_DynamicGISampleNormals", normalBuffer);
            shader.SetBuffer(kernel, "_DynamicGISampleResults", resultBuffer);
            shader.SetInt("_DynamicGISampleCount", 2);
            shader.Dispatch(kernel, 1, 1, 1);
            resultBuffer.GetData(results);
            return results;
        }

        private static int BuildAndReadDebug(WorldRadianceClipmap field, Vector3 center)
        {
            const int maximum = 8192;
            using GraphicsBuffer buffer = new(GraphicsBuffer.Target.Structured, maximum, RadianceDebugSampleGpu.Stride);
            if (!field.BuildDebugSamples(0, buffer, maximum, center, 0f, RadianceDebugDirection.Average, out int count))
                throw new InvalidOperationException("Clipmap rejected a debug build.");
            RadianceDebugSampleGpu[] values = new RadianceDebugSampleGpu[count];
            buffer.GetData(values, 0, 0, count);
            int valid = 0;
            for (int i = 0; i < values.Length; i++) if (values[i].ColorAndValidity.w > 0.001f) valid++;
            return valid;
        }

        private static void ValidateFinitePositive(Vector4 value, string label)
        {
            if (float.IsNaN(value.x) || float.IsInfinity(value.x) || Luminance(value) <= 0f)
                throw new InvalidOperationException($"Invalid {label} shader sample: {value}.");
        }

        private static float Luminance(Vector4 value) => value.x * 0.2126f + value.y * 0.7152f + value.z * 0.0722f;
        private static void Set(SerializedObject target, string name, float value) => target.FindProperty(name).floatValue = value;
        private static void Set(SerializedObject target, string name, int value) => target.FindProperty(name).intValue = value;
        private static void Set(SerializedObject target, string name, bool value) => target.FindProperty(name).boolValue = value;
        private static void Set(SerializedObject target, string name, Vector3 value) => target.FindProperty(name).vector3Value = value;
        private static void Set(SerializedObject target, string name, Color value) => target.FindProperty(name).colorValue = value;
        private static void Set(SerializedObject target, string name, UnityEngine.Object value) => target.FindProperty(name).objectReferenceValue = value;
    }
}
