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
    /// <summary>Portable GPU test for directional probes and east-window occlusion.</summary>
    public static class RadiancePhase4Validation
    {
        private const int ValidationLayer = 31;

        [MenuItem("Tools/Dynamic GI/Run Phase 4 Radiance Validation")]
        public static void Run()
        {
            Scene previousScene = SceneManager.GetActiveScene();
            NewSceneMode mode = Application.isBatchMode ? NewSceneMode.Single : NewSceneMode.Additive;
            Scene testScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, mode);
            SceneManager.SetActiveScene(testScene);
            List<GameObject> objects = new();

            try
            {
                BuildEastWindowRoom(objects);
                Light sun = CreateSun(objects);

                GameObject fieldObject = new("Phase 4 Validation Fields") { layer = ValidationLayer };
                objects.Add(fieldObject);
                fieldObject.SetActive(false);
                WorldGeometryField geometry = fieldObject.AddComponent<WorldGeometryField>();
                WorldSkyVisibilityField sky = fieldObject.AddComponent<WorldSkyVisibilityField>();
                WorldRadianceField radiance = fieldObject.AddComponent<WorldRadianceField>();
                RadianceFieldDebug fieldDebug = fieldObject.AddComponent<RadianceFieldDebug>();
                ConfigureGeometry(geometry);
                ConfigureSky(sky, geometry);
                ConfigureRadiance(radiance, geometry, sky, sun);
                ConfigureDebug(fieldDebug, radiance, fieldObject.transform);
                fieldObject.SetActive(true);

                RebuildAll(geometry, sky, radiance);

                Vector3 windowPathProbe = new(3.25f, 2.25f, 0.25f);
                Vector3 wallBlockedProbe = new(3.25f, 2.25f, 3.25f);
                RadianceProbeResult eastWindow = QuerySynchronously(radiance, windowPathProbe);
                RadianceProbeResult eastWall = QuerySynchronously(radiance, wallBlockedProbe);
                float windowPositiveX = Luminance(eastWindow.Radiance.PositiveX);
                float wallPositiveX = Luminance(eastWall.Radiance.PositiveX);
                if (windowPositiveX < wallPositiveX + 0.35f)
                {
                    throw new InvalidOperationException(
                        $"East window did not separate lit/blocked probes: window={windowPositiveX:0.000}, wall={wallPositiveX:0.000}.");
                }

                // Turn the light so the source is west. +X irradiance at the east
                // window must lose its direct-sun contribution without rebaking.
                sun.transform.rotation = Quaternion.LookRotation(new Vector3(1f, -0.15f, 0f).normalized, Vector3.up);
                radiance.RebuildAll();
                radiance.ProcessAllDirtyNow();
                RadianceProbeResult westSun = QuerySynchronously(radiance, windowPathProbe);
                float westPositiveX = Luminance(westSun.Radiance.PositiveX);
                if (westPositiveX > windowPositiveX - 0.35f)
                {
                    throw new InvalidOperationException(
                        $"Turning the Sun west did not reduce +X radiance: east={windowPositiveX:0.000}, west={westPositiveX:0.000}.");
                }

                int debugSamples = BuildAndReadDebug(radiance, windowPathProbe);
                if (debugSamples <= 0)
                    throw new InvalidOperationException("Radiance debug generated no valid samples.");
                fieldDebug.SendMessage("LateUpdate", SendMessageOptions.RequireReceiver);
                AsyncGPUReadback.WaitAllRequests();
                if (fieldDebug.NumericSampleCount <= 0)
                    throw new InvalidOperationException("Radiance numeric-label readback returned no samples.");

                RadianceFieldStats stats = radiance.Stats;
                Debug.Log(
                    $"DYNAMIC_GI_PHASE4_VALIDATION_PASSED | eastWindow+X={windowPositiveX:0.000} | " +
                    $"eastWall+X={wallPositiveX:0.000} | westSun+X={westPositiveX:0.000} | " +
                    $"resolution={stats.Resolution.x}x{stats.Resolution.y}x{stats.Resolution.z} | " +
                    $"probes={stats.ProbeCount} | debug={debugSamples} | labels={fieldDebug.NumericSampleCount} | " +
                    $"format={radiance.RadianceTextures[0].graphicsFormat} | GPU={EditorUtility.FormatBytes(stats.EstimatedGpuBytes)}");
            }
            finally
            {
                for (int i = objects.Count - 1; i >= 0; i--)
                {
                    if (objects[i] != null)
                        UnityEngine.Object.DestroyImmediate(objects[i]);
                }
                if (!Application.isBatchMode && testScene.IsValid() && testScene.isLoaded)
                    EditorSceneManager.CloseScene(testScene, true);
                if (previousScene.IsValid() && previousScene.isLoaded)
                    SceneManager.SetActiveScene(previousScene);
            }
        }

        private static void BuildEastWindowRoom(List<GameObject> objects)
        {
            CreateBlock(objects, "Floor", new Vector3(0f, 0f, 0f), new Vector3(10f, 0.25f, 8f));
            CreateBlock(objects, "Ceiling", new Vector3(0f, 5f, 0f), new Vector3(10f, 0.25f, 8f));
            CreateBlock(objects, "West Wall", new Vector3(-5f, 2.5f, 0f), new Vector3(0.25f, 5f, 8f));
            CreateBlock(objects, "North Wall", new Vector3(0f, 2.5f, 4f), new Vector3(10f, 5f, 0.25f));
            CreateBlock(objects, "South Wall", new Vector3(0f, 2.5f, -4f), new Vector3(10f, 5f, 0.25f));
            CreateBlock(objects, "East Bottom", new Vector3(5f, 0.6f, 0f), new Vector3(0.25f, 1.2f, 8f));
            CreateBlock(objects, "East Top", new Vector3(5f, 4.4f, 0f), new Vector3(0.25f, 1.2f, 8f));
            CreateBlock(objects, "East North", new Vector3(5f, 2.5f, 2.75f), new Vector3(0.25f, 2.6f, 2.5f));
            CreateBlock(objects, "East South", new Vector3(5f, 2.5f, -2.75f), new Vector3(0.25f, 2.6f, 2.5f));
        }

        private static GameObject CreateBlock(List<GameObject> objects, string name, Vector3 position, Vector3 scale)
        {
            GameObject block = GameObject.CreatePrimitive(PrimitiveType.Cube);
            block.name = name;
            block.layer = ValidationLayer;
            block.transform.position = position;
            block.transform.localScale = scale;
            objects.Add(block);
            return block;
        }

        private static Light CreateSun(List<GameObject> objects)
        {
            GameObject sunObject = new("East Sun") { layer = ValidationLayer };
            objects.Add(sunObject);
            Light sun = sunObject.AddComponent<Light>();
            sun.type = LightType.Directional;
            sun.color = new Color(1f, 0.9f, 0.75f);
            sun.intensity = 100000f;
            sunObject.transform.rotation = Quaternion.LookRotation(new Vector3(-1f, -0.15f, 0f).normalized, Vector3.up);
            return sun;
        }

        private static void ConfigureGeometry(WorldGeometryField field)
        {
            SerializedObject serialized = new(field);
            Set(serialized, "fieldCenter", new Vector3(0f, 3f, 0f));
            Set(serialized, "fieldSize", new Vector3(14f, 8f, 12f));
            Set(serialized, "voxelSize", 0.25f);
            Set(serialized, "brickResolution", 8);
            Set(serialized, "maxActiveBricks", 512);
            Set(serialized, "collectionMode", (int)GeometryCollectionMode.LayerMaskOnly);
            Set(serialized, "geometryLayers", 1 << ValidationLayer);
            Set(serialized, "rebuildBudgetBricksPerFrame", 64);
            Set(serialized, "voxelizationShader", AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/DynamicGI/Shaders/GeometryVoxelize.compute"));
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void ConfigureSky(WorldSkyVisibilityField field, WorldGeometryField geometry)
        {
            SerializedObject serialized = new(field);
            Set(serialized, "geometryField", geometry);
            Set(serialized, "sampleSpacing", 0.5f);
            Set(serialized, "rayCount", 24);
            Set(serialized, "maxTraceDistance", 16f);
            Set(serialized, "rayOriginBias", 0.05f);
            Set(serialized, "tileResolution", 4);
            Set(serialized, "updateBudgetTilesPerFrame", 64);
            Set(serialized, "skyVisibilityShader", AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/DynamicGI/Shaders/SkyVisibility.compute"));
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void ConfigureRadiance(
            WorldRadianceField field,
            WorldGeometryField geometry,
            WorldSkyVisibilityField sky,
            Light sun)
        {
            SerializedObject serialized = new(field);
            Set(serialized, "geometryField", geometry);
            Set(serialized, "skyVisibilityField", sky);
            Set(serialized, "sunLight", sun);
            Set(serialized, "fieldCenter", new Vector3(0f, 3f, 0f));
            Set(serialized, "fieldSize", new Vector3(12f, 6f, 10f));
            Set(serialized, "probeSpacing", 0.5f);
            Set(serialized, "followTarget", false);
            Set(serialized, "skyColor", new Color(0.2f, 0.35f, 0.65f));
            Set(serialized, "skyIntensity", 0.05f);
            Set(serialized, "sunIntensityScale", 0.00001f);
            Set(serialized, "sunTraceDistance", 32f);
            Set(serialized, "rayOriginBias", 0.05f);
            Set(serialized, "tileResolution", 4);
            Set(serialized, "updateBudgetTilesPerFrame", 64);
            Set(serialized, "radianceShader", AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/DynamicGI/Shaders/RadianceInject.compute"));
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void ConfigureDebug(RadianceFieldDebug fieldDebug, WorldRadianceField field, Transform center)
        {
            SerializedObject serialized = new(fieldDebug);
            Set(serialized, "radianceField", field);
            Set(serialized, "debugCenterTransform", center);
            Set(serialized, "probeQueryTarget", center);
            Set(serialized, "showRadianceProbes", true);
            Set(serialized, "showNumericValues", true);
            Set(serialized, "cameraRadius", 6f);
            Set(serialized, "maximumProbeInstances", 8192);
            Set(serialized, "maximumNumericLabels", 256);
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void RebuildAll(WorldGeometryField geometry, WorldSkyVisibilityField sky, WorldRadianceField radiance)
        {
            geometry.RebuildAll();
            geometry.ProcessAllDirtyNow();
            sky.RebuildAll();
            sky.ProcessAllDirtyNow();
            radiance.RebuildAll();
            radiance.ProcessAllDirtyNow();
        }

        private static RadianceProbeResult QuerySynchronously(WorldRadianceField field, Vector3 position)
        {
            RadianceProbeResult result = default;
            bool completed = false;
            if (!field.RequestProbe(position, value =>
                {
                    result = value;
                    completed = true;
                }))
            {
                throw new InvalidOperationException("Radiance Field rejected a GPU query.");
            }
            AsyncGPUReadback.WaitAllRequests();
            if (!completed || result.HasError)
                throw new InvalidOperationException("Radiance Field GPU query failed.");
            return result;
        }

        private static int BuildAndReadDebug(WorldRadianceField field, Vector3 center)
        {
            const int maximum = 8192;
            using GraphicsBuffer buffer = new(GraphicsBuffer.Target.Structured, maximum, RadianceDebugSampleGpu.Stride);
            if (!field.BuildDebugSamples(buffer, maximum, center, 6f, RadianceDebugDirection.Average, out int count))
                throw new InvalidOperationException("Radiance Field rejected a debug build.");
            RadianceDebugSampleGpu[] values = new RadianceDebugSampleGpu[count];
            buffer.GetData(values, 0, 0, count);
            int valid = 0;
            for (int i = 0; i < values.Length; i++)
            {
                if (values[i].ColorAndValidity.w > 0.001f)
                    valid++;
            }
            return valid;
        }

        private static float Luminance(Vector4 value) =>
            value.x * 0.2126f + value.y * 0.7152f + value.z * 0.0722f;

        private static void Set(SerializedObject target, string name, float value) => target.FindProperty(name).floatValue = value;
        private static void Set(SerializedObject target, string name, int value) => target.FindProperty(name).intValue = value;
        private static void Set(SerializedObject target, string name, bool value) => target.FindProperty(name).boolValue = value;
        private static void Set(SerializedObject target, string name, Vector3 value) => target.FindProperty(name).vector3Value = value;
        private static void Set(SerializedObject target, string name, Color value) => target.FindProperty(name).colorValue = value;
        private static void Set(SerializedObject target, string name, UnityEngine.Object value) => target.FindProperty(name).objectReferenceValue = value;
    }
}
