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
    /// Deterministic GPU validation for Phase 3. The room is generated from Unity
    /// primitives, so the test is portable and has no dependency on Test3/Polygon.
    /// </summary>
    public static class SkyVisibilityPhase3Validation
    {
        private const int ValidationLayer = 31;

        [MenuItem("Tools/Dynamic GI/Run Phase 3 Sky Visibility Validation")]
        public static void Run()
        {
            Scene previousActiveScene = SceneManager.GetActiveScene();
            // A fresh batchmode editor owns an unsaved untitled scene, and Unity does
            // not allow creating an additive scene on top of it. Interactive runs keep
            // the user's saved scene open; headless validation can safely replace it.
            NewSceneMode mode = Application.isBatchMode ? NewSceneMode.Single : NewSceneMode.Additive;
            Scene validationScene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, mode);
            SceneManager.SetActiveScene(validationScene);
            List<GameObject> objects = new();

            try
            {
                CreateBlock(objects, "Floor", new Vector3(0f, 0f, 0f), new Vector3(6f, 0.25f, 6f));
                GameObject ceiling = CreateBlock(objects, "Removable Ceiling", new Vector3(0f, 4f, 0f), new Vector3(6f, 0.25f, 6f));
                CreateBlock(objects, "West Wall", new Vector3(-3f, 2f, 0f), new Vector3(0.25f, 4f, 6f));
                CreateBlock(objects, "North Wall", new Vector3(0f, 2f, 3f), new Vector3(6f, 4f, 0.25f));
                CreateBlock(objects, "South Wall", new Vector3(0f, 2f, -3f), new Vector3(6f, 4f, 0.25f));

                // East wall with a 2 x 2.5 m door opening filled by a removable block.
                CreateBlock(objects, "East Wall Top", new Vector3(3f, 3.25f, 0f), new Vector3(0.25f, 1.5f, 6f));
                CreateBlock(objects, "East Wall North", new Vector3(3f, 1.25f, 2f), new Vector3(0.25f, 2.5f, 2f));
                CreateBlock(objects, "East Wall South", new Vector3(3f, 1.25f, -2f), new Vector3(0.25f, 2.5f, 2f));
                GameObject door = CreateBlock(objects, "Dynamic Door", new Vector3(3f, 1.25f, 0f), new Vector3(0.25f, 2.5f, 2f));

                GameObject fieldObject = new("Phase 3 Validation Fields") { layer = ValidationLayer };
                objects.Add(fieldObject);
                fieldObject.SetActive(false);
                WorldGeometryField geometryField = fieldObject.AddComponent<WorldGeometryField>();
                WorldSkyVisibilityField skyField = fieldObject.AddComponent<WorldSkyVisibilityField>();
                SkyVisibilityDebug skyDebug = fieldObject.AddComponent<SkyVisibilityDebug>();
                ConfigureGeometryField(geometryField);
                ConfigureSkyField(skyField, geometryField);
                ConfigureSkyDebug(skyDebug, skyField);
                fieldObject.SetActive(true);

                geometryField.RebuildAll();
                geometryField.ProcessAllDirtyNow();
                skyField.RebuildAll();
                skyField.ProcessAllDirtyNow();

                Vector3 enclosedPoint = new(0f, 2f, 0f);
                Vector3 outdoorPoint = new(4.75f, 2f, 0f);
                float enclosedVisibility = QuerySynchronously(skyField, enclosedPoint);
                float outdoorVisibility = QuerySynchronously(skyField, outdoorPoint);
                if (enclosedVisibility > 0.2f)
                    throw new InvalidOperationException($"Closed room is too open: {enclosedVisibility:0.000}.");
                if (outdoorVisibility < 0.6f)
                    throw new InvalidOperationException($"Outdoor sample is too occluded: {outdoorVisibility:0.000}.");

                Bounds doorBounds = door.GetComponent<Renderer>().bounds;
                door.GetComponent<Renderer>().enabled = false;
                geometryField.InvalidateRegion(doorBounds);
                geometryField.ProcessAllDirtyNow();
                int regionalDirtyTiles = skyField.DirtyTileCount;
                if (regionalDirtyTiles <= 0 || regionalDirtyTiles >= skyField.TotalTileCount)
                {
                    throw new InvalidOperationException(
                        $"Door invalidation was not local: {regionalDirtyTiles}/{skyField.TotalTileCount} tiles.");
                }
                skyField.ProcessAllDirtyNow();
                float doorOpenVisibility = QuerySynchronously(skyField, enclosedPoint);

                Bounds ceilingBounds = ceiling.GetComponent<Renderer>().bounds;
                ceiling.GetComponent<Renderer>().enabled = false;
                geometryField.InvalidateRegion(ceilingBounds);
                geometryField.ProcessAllDirtyNow();
                skyField.ProcessAllDirtyNow();
                float roofOpenVisibility = QuerySynchronously(skyField, enclosedPoint);
                if (roofOpenVisibility < enclosedVisibility + 0.2f)
                {
                    throw new InvalidOperationException(
                        $"Removing the roof did not increase visibility enough: closed={enclosedVisibility:0.000}, open={roofOpenVisibility:0.000}.");
                }

                int debugInstances = BuildAndCountDebugInstances(skyField, enclosedPoint);
                if (debugInstances <= 0)
                    throw new InvalidOperationException("Sky Visibility debug generated no instances.");
                VerifyDebugDoesNotClampToFieldEdge(skyField);
                skyDebug.SendMessage("LateUpdate", SendMessageOptions.RequireReceiver);

                SkyVisibilityStats stats = skyField.Stats;
                Debug.Log(
                    $"DYNAMIC_GI_PHASE3_VALIDATION_PASSED | closed={enclosedVisibility:0.000} | " +
                    $"outside={outdoorVisibility:0.000} | doorOpen={doorOpenVisibility:0.000} | " +
                    $"roofOpen={roofOpenVisibility:0.000} | localTiles={regionalDirtyTiles}/{skyField.TotalTileCount} | " +
                    $"resolution={stats.Resolution.x}x{stats.Resolution.y}x{stats.Resolution.z} | " +
                    $"format={skyField.VisibilityTexture.graphicsFormat} | GPU={EditorUtility.FormatBytes(stats.EstimatedGpuBytes)}");
            }
            finally
            {
                for (int i = objects.Count - 1; i >= 0; i--)
                {
                    if (objects[i] != null)
                        UnityEngine.Object.DestroyImmediate(objects[i]);
                }

                if (!Application.isBatchMode && validationScene.IsValid() && validationScene.isLoaded)
                    EditorSceneManager.CloseScene(validationScene, true);
                if (previousActiveScene.IsValid() && previousActiveScene.isLoaded)
                    SceneManager.SetActiveScene(previousActiveScene);
            }
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

        private static void ConfigureGeometryField(WorldGeometryField field)
        {
            SerializedObject serialized = new(field);
            serialized.FindProperty("fieldCenter").vector3Value = new Vector3(0f, 3f, 0f);
            serialized.FindProperty("fieldSize").vector3Value = new Vector3(12f, 8f, 12f);
            serialized.FindProperty("voxelSize").floatValue = 0.25f;
            serialized.FindProperty("brickResolution").intValue = 8;
            serialized.FindProperty("maxActiveBricks").intValue = 256;
            serialized.FindProperty("collectionMode").enumValueIndex = (int)GeometryCollectionMode.LayerMaskOnly;
            serialized.FindProperty("geometryLayers").intValue = 1 << ValidationLayer;
            serialized.FindProperty("rebuildOnEnable").boolValue = true;
            serialized.FindProperty("rebuildBudgetBricksPerFrame").intValue = 32;
            serialized.FindProperty("voxelizationShader").objectReferenceValue =
                AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/DynamicGI/Shaders/GeometryVoxelize.compute");
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void ConfigureSkyField(WorldSkyVisibilityField field, WorldGeometryField geometryField)
        {
            SerializedObject serialized = new(field);
            serialized.FindProperty("geometryField").objectReferenceValue = geometryField;
            serialized.FindProperty("sampleSpacing").floatValue = 0.5f;
            serialized.FindProperty("rayCount").intValue = 32;
            serialized.FindProperty("maxTraceDistance").floatValue = 4f;
            serialized.FindProperty("rayOriginBias").floatValue = 0.05f;
            serialized.FindProperty("tileResolution").intValue = 4;
            serialized.FindProperty("updateBudgetTilesPerFrame").intValue = 32;
            serialized.FindProperty("rebuildOnEnable").boolValue = true;
            serialized.FindProperty("skyVisibilityShader").objectReferenceValue =
                AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/DynamicGI/Shaders/SkyVisibility.compute");
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void ConfigureSkyDebug(SkyVisibilityDebug fieldDebug, WorldSkyVisibilityField skyField)
        {
            SerializedObject serialized = new(fieldDebug);
            serialized.FindProperty("skyVisibilityField").objectReferenceValue = skyField;
            serialized.FindProperty("showVisibilitySamples").boolValue = true;
            serialized.FindProperty("cameraRadius").floatValue = 5f;
            serialized.FindProperty("maximumSampleInstances").intValue = 32768;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static float QuerySynchronously(WorldSkyVisibilityField field, Vector3 position)
        {
            SkyVisibilityResult result = default;
            bool completed = false;
            bool accepted = field.RequestVisibility(position, value =>
            {
                result = value;
                completed = true;
            });
            if (!accepted)
                throw new InvalidOperationException("Sky Visibility rejected a GPU query.");

            AsyncGPUReadback.WaitAllRequests();
            if (!completed || result.HasError)
                throw new InvalidOperationException("Sky Visibility GPU query failed.");
            return result.Visibility;
        }

        private static int BuildAndCountDebugInstances(WorldSkyVisibilityField field, Vector3 center)
        {
            const int maximumInstances = 32768;
            using GraphicsBuffer instances = new(GraphicsBuffer.Target.Append, maximumInstances, sizeof(float) * 4);
            using GraphicsBuffer count = new(GraphicsBuffer.Target.Raw, 1, sizeof(uint));
            if (!field.BuildDebugInstances(instances, maximumInstances, center, 5f))
                throw new InvalidOperationException("Sky Visibility rejected the debug instance build.");

            GraphicsBuffer.CopyCount(instances, count, 0);
            uint[] result = new uint[1];
            count.GetData(result);
            return checked((int)result[0]);
        }

        private static void VerifyDebugDoesNotClampToFieldEdge(WorldSkyVisibilityField field)
        {
            const int maximumInstances = 256;
            using GraphicsBuffer instances = new(GraphicsBuffer.Target.Append, maximumInstances, sizeof(float) * 4);
            Vector3 farOutside = field.FieldBounds.max + Vector3.one * 100f;
            if (field.BuildDebugInstances(instances, maximumInstances, farOutside, 2f))
                throw new InvalidOperationException("Sky Visibility debug clamped an out-of-range camera to the field edge.");
        }
    }
}
