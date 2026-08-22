using System;
using System.Collections.Generic;
using DynamicGI.Debugging;
using DynamicGI.Geometry;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DynamicGI.Editor
{
    /// <summary>
    /// Reproducible setup and smoke validation for the Phase 2 laboratory scene.
    /// Keeping this as an editor command makes the scene configuration auditable and
    /// lets CI/unity-cli recreate it without hand-editing Unity YAML.
    /// </summary>
    public static class GeometryFieldPhase2SceneSetup
    {
        private const string TestScenePath = "Assets/Scenes/Test3.unity";
        private const string FieldObjectName = "World Geometry Field";
        private const string ComputePath = "Assets/DynamicGI/Shaders/GeometryVoxelize.compute";

        [MenuItem("Tools/Dynamic GI/Phase 2/Inspect Test3")]
        public static void InspectTest3()
        {
            Scene scene = OpenTestScene();
            SceneSummary summary = AnalyzeScene(scene);
            Debug.Log(summary.ToLogString("DYNAMIC_GI_PHASE2_SCENE_INSPECTION"));
        }

        [MenuItem("Tools/Dynamic GI/Phase 2/Configure Test3 Laboratory")]
        public static void ConfigureTest3()
        {
            Scene scene = OpenTestScene();
            SceneSummary summary = AnalyzeScene(scene);
            WorldGeometryField field = FindOrCreateField(scene);
            GeometryFieldDebug fieldDebug = field.GetComponent<GeometryFieldDebug>();
            if (fieldDebug == null)
                fieldDebug = field.gameObject.AddComponent<GeometryFieldDebug>();

            // Test3 is a large environment. A 0.5 m surface field keeps thin walls
            // inspectable while bounding the default brick pool to about 4 MiB.
            Bounds labBounds = CalculateLaboratoryBounds(summary.GeometryBounds);
            SerializedObject serializedField = new(field);
            SetVector3(serializedField, "fieldCenter", labBounds.center - field.transform.position);
            SetVector3(serializedField, "fieldSize", labBounds.size);
            SetFloat(serializedField, "voxelSize", 0.5f);
            SetInt(serializedField, "brickResolution", 16);
            SetInt(serializedField, "maxActiveBricks", 4096);
            SetInt(serializedField, "collectionMode", (int)GeometryCollectionMode.ExplicitContributorsAndLayerMask);
            SetInt(serializedField, "geometryLayers", ~(1 << 2)); // Ignore Raycast contains presentation helpers in Test3.
            SetBool(serializedField, "rebuildOnEnable", true);
            SetInt(serializedField, "rebuildBudgetBricksPerFrame", 8);
            SetInt(serializedField, "recentRegionLifetimeFrames", 180);
            SetObjectReference(serializedField, "voxelizationShader", AssetDatabase.LoadAssetAtPath<ComputeShader>(ComputePath));
            serializedField.ApplyModifiedPropertiesWithoutUndo();

            SerializedObject serializedDebug = new(fieldDebug);
            SetObjectReference(serializedDebug, "geometryField", field);
            SetObjectReference(serializedDebug, "targetCamera", summary.PreferredCamera);
            SetBool(serializedDebug, "showFieldBounds", true);
            SetBool(serializedDebug, "showActiveBricks", true);
            SetBool(serializedDebug, "showRecentlyRebuiltRegions", true);
            SetBool(serializedDebug, "showCameraNeighborhood", true);
            SetBool(serializedDebug, "showResolutionAndStats", true);
            SetBool(serializedDebug, "showOccupiedVoxels", true);
            SetBool(serializedDebug, "showEmptyVoxels", false);
            SetFloat(serializedDebug, "cameraRadius", 12f);
            SetInt(serializedDebug, "maximumVoxelInstances", 131072);
            SetFloat(serializedDebug, "voxelScale", 0.88f);
            SetBool(serializedDebug, "queryOccupancyAtCamera", false);
            serializedDebug.ApplyModifiedPropertiesWithoutUndo();

            EditorUtility.SetDirty(field);
            EditorUtility.SetDirty(fieldDebug);
            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene))
                throw new InvalidOperationException($"Could not save {TestScenePath}.");

            Debug.Log(
                $"DYNAMIC_GI_PHASE2_TEST3_CONFIGURED | bounds={FormatBounds(labBounds)} | " +
                $"camera={(summary.PreferredCamera != null ? summary.PreferredCamera.name : "Scene View fallback")} | " +
                "voxel=0.5m | brick=16^3 | capacity=4096");
        }

        [MenuItem("Tools/Dynamic GI/Phase 2/Validate Test3 Debug Field")]
        public static void ValidateTest3()
        {
            Scene scene = OpenTestScene();
            WorldGeometryField[] fields = FindSceneComponents<WorldGeometryField>(scene, true);
            if (fields.Length != 1)
                throw new InvalidOperationException($"Expected exactly one WorldGeometryField in Test3, found {fields.Length}.");

            WorldGeometryField field = fields[0];
            GeometryFieldDebug fieldDebug = field.GetComponent<GeometryFieldDebug>();
            if (fieldDebug == null)
                throw new InvalidOperationException("Test3 field has no GeometryFieldDebug component.");
            if (field.VoxelizationShader == null)
                throw new InvalidOperationException("Test3 field has no voxelization compute shader.");
            if (field.VoxelSize < 0.25f)
                throw new InvalidOperationException($"Test3 voxel size ({field.VoxelSize}) is unsafe for the full laboratory scene.");

            field.RebuildAll();
            field.ProcessAllDirtyNow();
            if (field.ActiveBrickCount <= 0)
                throw new InvalidOperationException("Test3 produced no active geometry bricks.");
            if (field.DirtyBrickCount != 0)
                throw new InvalidOperationException("Test3 still has dirty bricks after synchronous validation.");

            const int maximumDebugInstances = 131072;
            int occupiedInstances;
            int emptyInstances;
            int combinedInstances;
            using (GraphicsBuffer instances = new(GraphicsBuffer.Target.Append, maximumDebugInstances, sizeof(float) * 4))
            using (GraphicsBuffer count = new(GraphicsBuffer.Target.Raw, 1, sizeof(uint)))
            {
                occupiedInstances = BuildAndCountDebugInstances(field, instances, count, maximumDebugInstances, 0);
                emptyInstances = BuildAndCountDebugInstances(field, instances, count, maximumDebugInstances, 1);
                combinedInstances = BuildAndCountDebugInstances(field, instances, count, maximumDebugInstances, 2);
            }

            if (occupiedInstances <= 0)
                throw new InvalidOperationException("Occupied-voxel debug mode produced no instances in Test3.");
            if (emptyInstances <= 0)
                throw new InvalidOperationException("Empty-voxel debug mode produced no instances in Test3.");
            if (combinedInstances != occupiedInstances + emptyInstances)
                throw new InvalidOperationException(
                    $"Combined debug mode is inconsistent: occupied={occupiedInstances}, empty={emptyInstances}, both={combinedInstances}.");

            // LateUpdate exercises the camera-radius compute pass and HDRP indirect
            // draw setup. It intentionally does not require entering Play Mode.
            fieldDebug.SendMessage("LateUpdate", SendMessageOptions.RequireReceiver);

            Vector3Int resolution = field.FieldVoxelResolution;
            GeometryFieldStats stats = field.Stats;
            Debug.Log(
                $"DYNAMIC_GI_PHASE2_VALIDATION_PASSED | resolution={resolution.x}x{resolution.y}x{resolution.z} | " +
                $"bricks={stats.ActiveBricks} | debugBricks={field.LastDebugBrickCount} | " +
                $"debugSamples={field.LastDebugSampleCount} | stride={field.LastDebugSampleStride} | " +
                $"occupied={occupiedInstances} | empty={emptyInstances} | both={combinedInstances} | " +
                $"GPU={EditorUtility.FormatBytes(stats.EstimatedGpuBytes)}");
        }

        private static int BuildAndCountDebugInstances(
            WorldGeometryField field,
            GraphicsBuffer instances,
            GraphicsBuffer count,
            int maximumInstances,
            int mode)
        {
            if (!field.BuildDebugVoxelInstances(instances, maximumInstances, mode, field.transform.position, 12f))
                throw new InvalidOperationException($"Debug voxel mode {mode} rejected the instance build.");

            GraphicsBuffer.CopyCount(instances, count, 0);
            uint[] result = new uint[1];
            count.GetData(result);
            return checked((int)result[0]);
        }

        private static Scene OpenTestScene()
        {
            Scene scene = EditorSceneManager.OpenScene(TestScenePath, OpenSceneMode.Single);
            if (!scene.IsValid() || !scene.isLoaded)
                throw new InvalidOperationException($"Could not open {TestScenePath}.");
            return scene;
        }

        private static SceneSummary AnalyzeScene(Scene scene)
        {
            MeshRenderer[] renderers = FindSceneComponents<MeshRenderer>(scene, false);
            Bounds geometryBounds = default;
            bool hasBounds = false;
            int usableRendererCount = 0;

            for (int i = 0; i < renderers.Length; i++)
            {
                MeshRenderer renderer = renderers[i];
                MeshFilter filter = renderer.GetComponent<MeshFilter>();
                if (!renderer.enabled || !renderer.gameObject.activeInHierarchy || filter == null || filter.sharedMesh == null)
                    continue;
                if (renderer.gameObject.layer == 2)
                    continue;

                if (!hasBounds)
                {
                    geometryBounds = renderer.bounds;
                    hasBounds = true;
                }
                else
                {
                    geometryBounds.Encapsulate(renderer.bounds);
                }

                usableRendererCount++;
            }

            if (!hasBounds)
                throw new InvalidOperationException("Test3 contains no usable MeshRenderer geometry.");

            Camera[] cameras = FindSceneComponents<Camera>(scene, true);
            Camera preferredCamera = null;
            for (int i = 0; i < cameras.Length; i++)
            {
                if (cameras[i].isActiveAndEnabled && cameras[i].CompareTag("MainCamera"))
                {
                    preferredCamera = cameras[i];
                    break;
                }
            }

            if (preferredCamera == null)
            {
                for (int i = 0; i < cameras.Length; i++)
                {
                    if (cameras[i].isActiveAndEnabled)
                    {
                        preferredCamera = cameras[i];
                        break;
                    }
                }
            }

            return new SceneSummary(renderers.Length, usableRendererCount, cameras.Length, geometryBounds, preferredCamera);
        }

        private static Bounds CalculateLaboratoryBounds(Bounds geometryBounds)
        {
            // Keep Test3's intended 128 x 32 x 128 laboratory footprint while
            // centering it on real content. Tall/distant background dressing is
            // deliberately outside this local Geometry Field prototype.
            Vector3 size = new(
                Mathf.Min(128f, Mathf.Ceil(geometryBounds.size.x / 8f) * 8f),
                Mathf.Min(32f, Mathf.Ceil(geometryBounds.size.y / 8f) * 8f),
                Mathf.Min(128f, Mathf.Ceil(geometryBounds.size.z / 8f) * 8f));
            size = Vector3.Max(size, new Vector3(16f, 8f, 16f));

            Vector3 center = geometryBounds.center;
            center.y = geometryBounds.min.y + size.y * 0.5f;
            center.x = Mathf.Round(center.x / 4f) * 4f;
            center.y = Mathf.Round(center.y / 4f) * 4f;
            center.z = Mathf.Round(center.z / 4f) * 4f;
            return new Bounds(center, size);
        }

        private static WorldGeometryField FindOrCreateField(Scene scene)
        {
            WorldGeometryField[] fields = FindSceneComponents<WorldGeometryField>(scene, true);
            if (fields.Length > 1)
                throw new InvalidOperationException($"Test3 contains {fields.Length} WorldGeometryField components; refusing to choose one implicitly.");
            if (fields.Length == 1)
                return fields[0];

            GameObject fieldObject = new(FieldObjectName);
            SceneManager.MoveGameObjectToScene(fieldObject, scene);
            return fieldObject.AddComponent<WorldGeometryField>();
        }

        private static T[] FindSceneComponents<T>(Scene scene, bool includeInactive) where T : Component
        {
            List<T> results = new();
            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
                results.AddRange(roots[i].GetComponentsInChildren<T>(includeInactive));
            return results.ToArray();
        }

        private static void SetVector3(SerializedObject serializedObject, string name, Vector3 value)
        {
            serializedObject.FindProperty(name).vector3Value = value;
        }

        private static void SetFloat(SerializedObject serializedObject, string name, float value)
        {
            serializedObject.FindProperty(name).floatValue = value;
        }

        private static void SetInt(SerializedObject serializedObject, string name, int value)
        {
            serializedObject.FindProperty(name).intValue = value;
        }

        private static void SetBool(SerializedObject serializedObject, string name, bool value)
        {
            serializedObject.FindProperty(name).boolValue = value;
        }

        private static void SetObjectReference(SerializedObject serializedObject, string name, UnityEngine.Object value)
        {
            serializedObject.FindProperty(name).objectReferenceValue = value;
        }

        private static string FormatBounds(Bounds bounds)
        {
            return $"center({bounds.center.x:0.##},{bounds.center.y:0.##},{bounds.center.z:0.##})/" +
                   $"size({bounds.size.x:0.##},{bounds.size.y:0.##},{bounds.size.z:0.##})";
        }

        private readonly struct SceneSummary
        {
            public readonly int RendererCount;
            public readonly int UsableRendererCount;
            public readonly int CameraCount;
            public readonly Bounds GeometryBounds;
            public readonly Camera PreferredCamera;

            public SceneSummary(
                int rendererCount,
                int usableRendererCount,
                int cameraCount,
                Bounds geometryBounds,
                Camera preferredCamera)
            {
                RendererCount = rendererCount;
                UsableRendererCount = usableRendererCount;
                CameraCount = cameraCount;
                GeometryBounds = geometryBounds;
                PreferredCamera = preferredCamera;
            }

            public string ToLogString(string prefix)
            {
                return $"{prefix} | meshRenderers={RendererCount} | usable={UsableRendererCount} | " +
                       $"cameras={CameraCount} | preferredCamera={(PreferredCamera != null ? PreferredCamera.name : "none")} | " +
                       $"geometry={FormatBounds(GeometryBounds)}";
            }
        }
    }
}
