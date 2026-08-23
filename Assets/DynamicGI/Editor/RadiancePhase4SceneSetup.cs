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
    /// <summary>Creates and validates the Phase 4 east-window laboratory in TestGI.</summary>
    public static class RadiancePhase4SceneSetup
    {
        private const string ScenePath = "Assets/Scenes/TestGI.unity";
        private const string RoomRootName = "Dynamic GI Phase 4 - East Window Room";
        private const string FieldRootName = "Dynamic GI Phase 4 Fields";
        private const string MaterialFolder = "Assets/Scenes/TestGI/DynamicGI Phase4 Materials";
        private const int GeometryLayer = 30;

        [MenuItem("Tools/Dynamic GI/Phase 4/Configure TestGI East Window Room")]
        public static void ConfigureTestGI()
        {
            Scene scene = OpenScene();
            DestroyGeneratedRoot(scene, RoomRootName);
            DestroyGeneratedRoot(scene, FieldRootName);

            Material wallMaterial = GetOrCreateMaterial("Wall", new Color(0.16f, 0.2f, 0.28f), 0.18f);
            Material floorMaterial = GetOrCreateMaterial("Floor", new Color(0.12f, 0.14f, 0.17f), 0.3f);
            Material trimMaterial = GetOrCreateMaterial("East Window Trim", new Color(0.05f, 0.65f, 0.85f), 0.45f);

            GameObject roomRoot = new(RoomRootName) { layer = GeometryLayer };
            SceneManager.MoveGameObjectToScene(roomRoot, scene);
            CreateBlock(roomRoot.transform, "Floor", new Vector3(0f, 0f, 0f), new Vector3(10f, 0.25f, 8f), floorMaterial);
            CreateBlock(roomRoot.transform, "Ceiling", new Vector3(0f, 5f, 0f), new Vector3(10f, 0.25f, 8f), wallMaterial);
            CreateBlock(roomRoot.transform, "West Wall", new Vector3(-5f, 2.5f, 0f), new Vector3(0.25f, 5f, 8f), wallMaterial);
            CreateBlock(roomRoot.transform, "North Wall", new Vector3(0f, 2.5f, 4f), new Vector3(10f, 5f, 0.25f), wallMaterial);
            CreateBlock(roomRoot.transform, "South Wall", new Vector3(0f, 2.5f, -4f), new Vector3(10f, 5f, 0.25f), wallMaterial);
            CreateBlock(roomRoot.transform, "East Bottom", new Vector3(5f, 0.6f, 0f), new Vector3(0.25f, 1.2f, 8f), wallMaterial);
            CreateBlock(roomRoot.transform, "East Top", new Vector3(5f, 4.4f, 0f), new Vector3(0.25f, 1.2f, 8f), wallMaterial);
            CreateBlock(roomRoot.transform, "East North", new Vector3(5f, 2.5f, 2.75f), new Vector3(0.25f, 2.6f, 2.5f), wallMaterial);
            CreateBlock(roomRoot.transform, "East South", new Vector3(5f, 2.5f, -2.75f), new Vector3(0.25f, 2.6f, 2.5f), wallMaterial);
            CreateBlock(roomRoot.transform, "Window Trim Top", new Vector3(4.82f, 3.85f, 0f), new Vector3(0.18f, 0.12f, 3.2f), trimMaterial);
            CreateBlock(roomRoot.transform, "Window Trim Bottom", new Vector3(4.82f, 1.15f, 0f), new Vector3(0.18f, 0.12f, 3.2f), trimMaterial);
            CreateBlock(roomRoot.transform, "Window Trim North", new Vector3(4.82f, 2.5f, 1.55f), new Vector3(0.18f, 2.8f, 0.12f), trimMaterial);
            CreateBlock(roomRoot.transform, "Window Trim South", new Vector3(4.82f, 2.5f, -1.55f), new Vector3(0.18f, 2.8f, 0.12f), trimMaterial);

            Transform centerMarker = CreateMarker(roomRoot.transform, "Radiance Debug Center", new Vector3(0f, 2.5f, 0f));
            Transform windowMarker = CreateMarker(roomRoot.transform, "East Window Marker", new Vector3(5f, 2.5f, 0f));
            Transform litMarker = CreateMarker(roomRoot.transform, "Lit Probe Marker", new Vector3(3.25f, 2.25f, 0.25f));
            Transform blockedMarker = CreateMarker(roomRoot.transform, "Blocked Probe Marker", new Vector3(3.25f, 2.25f, 3.25f));

            Light sun = FindDirectionalSun(scene);
            sun.transform.rotation = Quaternion.LookRotation(new Vector3(-1f, -0.15f, 0f).normalized, Vector3.up);
            sun.color = new Color(1f, 0.9f, 0.75f);
            sun.intensity = 130000f;

            GameObject fieldObject = new(FieldRootName);
            SceneManager.MoveGameObjectToScene(fieldObject, scene);
            fieldObject.SetActive(false);
            WorldGeometryField geometry = fieldObject.AddComponent<WorldGeometryField>();
            GeometryFieldDebug geometryDebug = fieldObject.AddComponent<GeometryFieldDebug>();
            WorldSkyVisibilityField sky = fieldObject.AddComponent<WorldSkyVisibilityField>();
            SkyVisibilityDebug skyDebug = fieldObject.AddComponent<SkyVisibilityDebug>();
            WorldRadianceField radiance = fieldObject.AddComponent<WorldRadianceField>();
            RadianceFieldDebug radianceDebug = fieldObject.AddComponent<RadianceFieldDebug>();
            ConfigureGeometry(geometry);
            ConfigureGeometryDebug(geometryDebug, geometry);
            ConfigureSky(sky, geometry);
            ConfigureSkyDebug(skyDebug, sky);
            ConfigureRadiance(radiance, geometry, sky, sun);
            ConfigureRadianceDebug(radianceDebug, radiance, centerMarker, litMarker);
            fieldObject.SetActive(true);

            Phase4TestRoomGuide guide = roomRoot.AddComponent<Phase4TestRoomGuide>();
            SerializedObject serializedGuide = new(guide);
            Set(serializedGuide, "eastWindowMarker", windowMarker);
            Set(serializedGuide, "litProbeMarker", litMarker);
            Set(serializedGuide, "blockedProbeMarker", blockedMarker);
            Set(serializedGuide, "sunLight", sun);
            Set(serializedGuide, "roomCenter", new Vector3(0f, 2.5f, 0f));
            serializedGuide.ApplyModifiedPropertiesWithoutUndo();

            Camera camera = FindMainCamera(scene);
            if (camera != null)
            {
                // Look straight through the east opening so Game view immediately
                // exposes the lit probe corridor instead of the room's exterior wall.
                camera.usePhysicalProperties = false;
                camera.fieldOfView = 55f;
                camera.transform.position = new Vector3(12f, 3.4f, 0f);
                camera.transform.rotation = Quaternion.LookRotation(new Vector3(0f, 2.4f, 0f) - camera.transform.position, Vector3.up);
            }

            EditorUtility.SetDirty(roomRoot);
            EditorUtility.SetDirty(fieldObject);
            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene))
                throw new InvalidOperationException($"Could not save {ScenePath}.");

            Debug.Log(
                "DYNAMIC_GI_PHASE4_TESTGI_CONFIGURED | room=10x5x8m | window=east/+X | " +
                "geometryVoxel=0.25m | sky=0.5m/24rays | radiance=0.5m/6RGB | numericLabels=512/slice");
        }

        [MenuItem("Tools/Dynamic GI/Phase 4/Validate TestGI East Window Room")]
        public static void ValidateTestGI()
        {
            Scene scene = OpenScene();
            WorldGeometryField geometry = FindSingle<WorldGeometryField>(scene);
            WorldSkyVisibilityField sky = FindSingle<WorldSkyVisibilityField>(scene);
            WorldRadianceField radiance = FindSingle<WorldRadianceField>(scene);
            RadianceFieldDebug fieldDebug = FindSingle<RadianceFieldDebug>(scene);
            Transform lit = FindNamedTransform(scene, "Lit Probe Marker");
            Transform blocked = FindNamedTransform(scene, "Blocked Probe Marker");

            geometry.RebuildAll();
            geometry.ProcessAllDirtyNow();
            sky.RebuildAll();
            sky.ProcessAllDirtyNow();
            radiance.RebuildAll();
            radiance.ProcessAllDirtyNow();

            RadianceProbeResult litResult = Query(radiance, lit.position);
            RadianceProbeResult blockedResult = Query(radiance, blocked.position);
            float litValue = Luminance(litResult.Radiance.PositiveX);
            float blockedValue = Luminance(blockedResult.Radiance.PositiveX);
            if (litValue < blockedValue + 0.35f)
                throw new InvalidOperationException($"TestGI wall/window separation failed: {litValue:0.000} vs {blockedValue:0.000}.");

            fieldDebug.SendMessage("LateUpdate", SendMessageOptions.RequireReceiver);
            AsyncGPUReadback.WaitAllRequests();
            if (fieldDebug.NumericSampleCount <= 0)
                throw new InvalidOperationException("TestGI numeric probe labels received no GPU values.");

            RadianceFieldStats stats = radiance.Stats;
            Debug.Log(
                $"DYNAMIC_GI_PHASE4_TESTGI_VALIDATION_PASSED | lit+X={litValue:0.000} | blocked+X={blockedValue:0.000} | " +
                $"resolution={stats.Resolution.x}x{stats.Resolution.y}x{stats.Resolution.z} | probes={stats.ProbeCount} | " +
                $"labels={fieldDebug.NumericSampleCount} | GPU={EditorUtility.FormatBytes(stats.EstimatedGpuBytes)}");
        }

        private static void ConfigureGeometry(WorldGeometryField field)
        {
            SerializedObject value = new(field);
            Set(value, "fieldCenter", new Vector3(0f, 3f, 0f));
            Set(value, "fieldSize", new Vector3(14f, 8f, 12f));
            Set(value, "voxelSize", 0.25f);
            Set(value, "brickResolution", 8);
            Set(value, "maxActiveBricks", 512);
            Set(value, "collectionMode", (int)GeometryCollectionMode.LayerMaskOnly);
            Set(value, "geometryLayers", 1 << GeometryLayer);
            Set(value, "rebuildBudgetBricksPerFrame", 32);
            Set(value, "voxelizationShader", AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/DynamicGI/Shaders/GeometryVoxelize.compute"));
            value.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void ConfigureGeometryDebug(GeometryFieldDebug fieldDebug, WorldGeometryField field)
        {
            SerializedObject value = new(fieldDebug);
            Set(value, "geometryField", field);
            Set(value, "showFieldBounds", true);
            Set(value, "showActiveBricks", false);
            Set(value, "showRecentlyRebuiltRegions", false);
            Set(value, "showCameraNeighborhood", false);
            Set(value, "showResolutionAndStats", false);
            Set(value, "showOccupiedVoxels", false);
            Set(value, "showEmptyVoxels", false);
            value.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void ConfigureSky(WorldSkyVisibilityField field, WorldGeometryField geometry)
        {
            SerializedObject value = new(field);
            Set(value, "geometryField", geometry);
            Set(value, "sampleSpacing", 0.5f);
            Set(value, "rayCount", 24);
            Set(value, "maxTraceDistance", 16f);
            Set(value, "rayOriginBias", 0.05f);
            Set(value, "tileResolution", 4);
            Set(value, "updateBudgetTilesPerFrame", 32);
            Set(value, "skyVisibilityShader", AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/DynamicGI/Shaders/SkyVisibility.compute"));
            value.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void ConfigureSkyDebug(SkyVisibilityDebug fieldDebug, WorldSkyVisibilityField field)
        {
            SerializedObject value = new(fieldDebug);
            Set(value, "skyVisibilityField", field);
            Set(value, "showFieldBounds", false);
            Set(value, "showDirtyTiles", false);
            Set(value, "showRecentlyUpdatedTiles", false);
            Set(value, "showCameraNeighborhood", false);
            Set(value, "showResolutionAndStats", false);
            Set(value, "showVisibilitySamples", false);
            value.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void ConfigureRadiance(
            WorldRadianceField field,
            WorldGeometryField geometry,
            WorldSkyVisibilityField sky,
            Light sun)
        {
            SerializedObject value = new(field);
            Set(value, "geometryField", geometry);
            Set(value, "skyVisibilityField", sky);
            Set(value, "sunLight", sun);
            Set(value, "fieldCenter", new Vector3(0f, 3f, 0f));
            Set(value, "fieldSize", new Vector3(12f, 6f, 10f));
            Set(value, "probeSpacing", 0.5f);
            Set(value, "followTarget", false);
            Set(value, "skyColor", new Color(0.2f, 0.35f, 0.65f));
            Set(value, "skyIntensity", 0.05f);
            Set(value, "sunIntensityScale", 0.00001f);
            Set(value, "sunTraceDistance", 32f);
            Set(value, "rayOriginBias", 0.05f);
            Set(value, "tileResolution", 4);
            Set(value, "updateBudgetTilesPerFrame", 16);
            Set(value, "radianceShader", AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/DynamicGI/Shaders/RadianceInject.compute"));
            value.ApplyModifiedPropertiesWithoutUndo();
        }

        private static void ConfigureRadianceDebug(
            RadianceFieldDebug fieldDebug,
            WorldRadianceField field,
            Transform center,
            Transform queryTarget)
        {
            SerializedObject value = new(fieldDebug);
            Set(value, "radianceField", field);
            Set(value, "debugCenterTransform", center);
            Set(value, "probeQueryTarget", queryTarget);
            Set(value, "showFieldBounds", true);
            Set(value, "showDirtyTiles", true);
            Set(value, "showRecentlyUpdatedTiles", true);
            Set(value, "showCameraNeighborhood", false);
            Set(value, "showProbeGizmos", true);
            Set(value, "showSunDirection", true);
            Set(value, "showResolutionAndStats", true);
            Set(value, "showRadianceProbes", true);
            Set(value, "displayedDirection", (int)RadianceDebugDirection.PositiveX);
            Set(value, "cameraRadius", 6f);
            Set(value, "maximumProbeInstances", 8192);
            Set(value, "probeScale", 0.22f);
            Set(value, "exposure", 1.25f);
            Set(value, "showNumericValues", true);
            Set(value, "maximumNumericLabels", 512);
            Set(value, "numericHorizontalSliceOnly", true);
            Set(value, "queryDetailedProbe", true);
            value.ApplyModifiedPropertiesWithoutUndo();
        }

        private static GameObject CreateBlock(Transform parent, string name, Vector3 position, Vector3 scale, Material material)
        {
            GameObject block = GameObject.CreatePrimitive(PrimitiveType.Cube);
            block.name = name;
            block.layer = GeometryLayer;
            block.transform.SetParent(parent, true);
            block.transform.position = position;
            block.transform.localScale = scale;
            block.GetComponent<Renderer>().sharedMaterial = material;
            return block;
        }

        private static Transform CreateMarker(Transform parent, string name, Vector3 position)
        {
            GameObject marker = new(name) { layer = GeometryLayer };
            marker.transform.SetParent(parent, true);
            marker.transform.position = position;
            return marker.transform;
        }

        private static Material GetOrCreateMaterial(string name, Color color, float smoothness)
        {
            EnsureAssetFolder(MaterialFolder);
            string path = $"{MaterialFolder}/{name}.mat";
            Material material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                Shader shader = Shader.Find("HDRP/Lit");
                if (shader == null)
                    throw new InvalidOperationException("HDRP/Lit shader was not found.");
                material = new Material(shader) { name = name };
                AssetDatabase.CreateAsset(material, path);
            }
            if (material.HasProperty("_BaseColor")) material.SetColor("_BaseColor", color);
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", smoothness);
            EditorUtility.SetDirty(material);
            return material;
        }

        private static void EnsureAssetFolder(string path)
        {
            string[] parts = path.Split('/');
            string current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                string next = $"{current}/{parts[i]}";
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }

        private static void DestroyGeneratedRoot(Scene scene, string name)
        {
            GameObject[] roots = scene.GetRootGameObjects();
            for (int i = 0; i < roots.Length; i++)
            {
                if (roots[i].name == name)
                    UnityEngine.Object.DestroyImmediate(roots[i]);
            }
        }

        private static Scene OpenScene()
        {
            Scene scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            if (!scene.IsValid() || !scene.isLoaded)
                throw new InvalidOperationException($"Could not open {ScenePath}.");
            return scene;
        }

        private static Light FindDirectionalSun(Scene scene)
        {
            Light[] lights = FindComponents<Light>(scene);
            for (int i = 0; i < lights.Length; i++)
            {
                if (lights[i].type == LightType.Directional)
                    return lights[i];
            }
            GameObject sunObject = new("Sun");
            SceneManager.MoveGameObjectToScene(sunObject, scene);
            Light sun = sunObject.AddComponent<Light>();
            sun.type = LightType.Directional;
            return sun;
        }

        private static Camera FindMainCamera(Scene scene)
        {
            Camera[] cameras = FindComponents<Camera>(scene);
            for (int i = 0; i < cameras.Length; i++)
                if (cameras[i].CompareTag("MainCamera")) return cameras[i];
            return cameras.Length > 0 ? cameras[0] : null;
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

        private static RadianceProbeResult Query(WorldRadianceField field, Vector3 position)
        {
            RadianceProbeResult result = default;
            bool complete = false;
            if (!field.RequestProbe(position, value => { result = value; complete = true; }))
                throw new InvalidOperationException("TestGI Radiance Field rejected a GPU query.");
            AsyncGPUReadback.WaitAllRequests();
            if (!complete || result.HasError)
                throw new InvalidOperationException("TestGI Radiance query failed.");
            return result;
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
