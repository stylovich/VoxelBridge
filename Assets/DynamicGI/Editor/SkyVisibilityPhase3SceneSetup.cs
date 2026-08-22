using System;
using System.Collections.Generic;
using DynamicGI.Debugging;
using DynamicGI.Geometry;
using DynamicGI.Occlusion;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace DynamicGI.Editor
{
    /// <summary>
    /// Installs the Phase 3 field in the local Test3 laboratory. The portable GPU
    /// validation remains independent of this asset-heavy scene.
    /// </summary>
    public static class SkyVisibilityPhase3SceneSetup
    {
        private const string TestScenePath = "Assets/Scenes/Test3.unity";
        private const string ComputePath = "Assets/DynamicGI/Shaders/SkyVisibility.compute";

        [MenuItem("Tools/Dynamic GI/Phase 3/Configure Test3 Sky Visibility")]
        public static void ConfigureTest3()
        {
            Scene scene = OpenTestScene();
            WorldGeometryField geometryField = FindSingle<WorldGeometryField>(scene, "WorldGeometryField");
            WorldSkyVisibilityField skyField = geometryField.GetComponent<WorldSkyVisibilityField>();
            if (skyField == null)
                skyField = geometryField.gameObject.AddComponent<WorldSkyVisibilityField>();

            SkyVisibilityDebug skyDebug = geometryField.GetComponent<SkyVisibilityDebug>();
            if (skyDebug == null)
                skyDebug = geometryField.gameObject.AddComponent<SkyVisibilityDebug>();

            Camera camera = FindPreferredCamera(scene);
            SerializedObject serializedField = new(skyField);
            SetReference(serializedField, "geometryField", geometryField);
            SetFloat(serializedField, "sampleSpacing", 1f);
            SetInt(serializedField, "rayCount", 16);
            SetFloat(serializedField, "maxTraceDistance", 32f);
            SetFloat(serializedField, "rayOriginBias", 0.08f);
            SetInt(serializedField, "tileResolution", 8);
            SetInt(serializedField, "updateBudgetTilesPerFrame", 8);
            SetBool(serializedField, "rebuildOnEnable", true);
            SetInt(serializedField, "recentRegionLifetimeFrames", 180);
            SetReference(serializedField, "skyVisibilityShader", AssetDatabase.LoadAssetAtPath<ComputeShader>(ComputePath));
            serializedField.ApplyModifiedPropertiesWithoutUndo();

            SerializedObject serializedDebug = new(skyDebug);
            SetReference(serializedDebug, "skyVisibilityField", skyField);
            SetReference(serializedDebug, "targetCamera", camera);
            SetBool(serializedDebug, "showFieldBounds", true);
            SetBool(serializedDebug, "showDirtyTiles", true);
            SetBool(serializedDebug, "showRecentlyUpdatedTiles", true);
            SetBool(serializedDebug, "showCameraNeighborhood", true);
            SetBool(serializedDebug, "showResolutionAndStats", true);
            SetBool(serializedDebug, "showVisibilitySamples", true);
            SetFloat(serializedDebug, "cameraRadius", 8f);
            SetInt(serializedDebug, "maximumSampleInstances", 32768);
            SetFloat(serializedDebug, "sampleScale", 0.22f);
            SetFloat(serializedDebug, "contrast", 1.25f);
            SetBool(serializedDebug, "queryVisibilityAtCamera", false);
            serializedDebug.ApplyModifiedPropertiesWithoutUndo();

            EditorUtility.SetDirty(skyField);
            EditorUtility.SetDirty(skyDebug);
            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene))
                throw new InvalidOperationException($"Could not save {TestScenePath}.");

            Vector3Int resolution = skyField.Resolution;
            Debug.Log(
                $"DYNAMIC_GI_PHASE3_TEST3_CONFIGURED | resolution={resolution.x}x{resolution.y}x{resolution.z} | " +
                "spacing=1m | rays=16 | trace=32m | tile=8^3 | budget=8/frame");
        }

        [MenuItem("Tools/Dynamic GI/Phase 3/Validate Test3 Sky Visibility")]
        public static void ValidateTest3()
        {
            Scene scene = OpenTestScene();
            WorldGeometryField geometryField = FindSingle<WorldGeometryField>(scene, "WorldGeometryField");
            WorldSkyVisibilityField skyField = FindSingle<WorldSkyVisibilityField>(scene, "WorldSkyVisibilityField");
            SkyVisibilityDebug skyDebug = FindSingle<SkyVisibilityDebug>(scene, "SkyVisibilityDebug");

            if (skyField.GeometryField != geometryField)
                throw new InvalidOperationException("Test3 Sky Visibility is not linked to its Geometry Field.");

            geometryField.RebuildAll();
            geometryField.ProcessAllDirtyNow();
            skyField.RebuildAll();
            skyField.ProcessAllDirtyNow();
            if (skyField.DirtyTileCount != 0)
                throw new InvalidOperationException($"Test3 still has {skyField.DirtyTileCount} dirty Sky Visibility tiles.");

            Vector3 queryPosition = skyField.FieldBounds.center;
            float visibility = QuerySynchronously(skyField, queryPosition);
            if (visibility < 0f || visibility > 1f)
                throw new InvalidOperationException($"Invalid Test3 sky visibility value: {visibility}.");

            const int maximumInstances = 32768;
            int debugInstances;
            using (GraphicsBuffer instances = new(GraphicsBuffer.Target.Append, maximumInstances, sizeof(float) * 4))
            using (GraphicsBuffer count = new(GraphicsBuffer.Target.Raw, 1, sizeof(uint)))
            {
                if (!skyField.BuildDebugInstances(instances, maximumInstances, queryPosition, 8f))
                    throw new InvalidOperationException("Test3 rejected the Sky Visibility debug build.");
                GraphicsBuffer.CopyCount(instances, count, 0);
                uint[] result = new uint[1];
                count.GetData(result);
                debugInstances = checked((int)result[0]);
            }

            if (debugInstances <= 0)
                throw new InvalidOperationException("Test3 produced no Sky Visibility debug instances.");
            skyDebug.SendMessage("LateUpdate", SendMessageOptions.RequireReceiver);

            SkyVisibilityStats stats = skyField.Stats;
            Debug.Log(
                $"DYNAMIC_GI_PHASE3_TEST3_VALIDATION_PASSED | visibility={visibility:0.000} | " +
                $"resolution={stats.Resolution.x}x{stats.Resolution.y}x{stats.Resolution.z} | " +
                $"tiles={stats.TotalTiles} | debug={debugInstances} | format={skyField.VisibilityTexture.graphicsFormat} | " +
                $"GPU={EditorUtility.FormatBytes(stats.EstimatedGpuBytes)}");
        }

        private static float QuerySynchronously(WorldSkyVisibilityField field, Vector3 position)
        {
            SkyVisibilityResult result = default;
            bool complete = false;
            if (!field.RequestVisibility(position, value =>
                {
                    result = value;
                    complete = true;
                }))
            {
                throw new InvalidOperationException("Test3 rejected the Sky Visibility GPU query.");
            }

            AsyncGPUReadback.WaitAllRequests();
            if (!complete || result.HasError)
                throw new InvalidOperationException("Test3 Sky Visibility GPU query failed.");
            return result.Visibility;
        }

        private static Scene OpenTestScene()
        {
            Scene scene = EditorSceneManager.OpenScene(TestScenePath, OpenSceneMode.Single);
            if (!scene.IsValid() || !scene.isLoaded)
                throw new InvalidOperationException($"Could not open {TestScenePath}.");
            return scene;
        }

        private static T FindSingle<T>(Scene scene, string label) where T : Component
        {
            List<T> results = new();
            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
                results.AddRange(roots[i].GetComponentsInChildren<T>(true));
            if (results.Count != 1)
                throw new InvalidOperationException($"Expected one {label} in Test3, found {results.Count}.");
            return results[0];
        }

        private static Camera FindPreferredCamera(Scene scene)
        {
            Camera fallback = null;
            GameObject[] roots = scene.GetRootGameObjects();
            for (int rootIndex = 0; rootIndex < roots.Length; rootIndex++)
            {
                Camera[] cameras = roots[rootIndex].GetComponentsInChildren<Camera>(true);
                for (int i = 0; i < cameras.Length; i++)
                {
                    if (!cameras[i].isActiveAndEnabled)
                        continue;
                    fallback ??= cameras[i];
                    if (cameras[i].CompareTag("MainCamera"))
                        return cameras[i];
                }
            }
            return fallback;
        }

        private static void SetFloat(SerializedObject target, string property, float value) =>
            target.FindProperty(property).floatValue = value;

        private static void SetInt(SerializedObject target, string property, int value) =>
            target.FindProperty(property).intValue = value;

        private static void SetBool(SerializedObject target, string property, bool value) =>
            target.FindProperty(property).boolValue = value;

        private static void SetReference(SerializedObject target, string property, UnityEngine.Object value) =>
            target.FindProperty(property).objectReferenceValue = value;
    }
}
