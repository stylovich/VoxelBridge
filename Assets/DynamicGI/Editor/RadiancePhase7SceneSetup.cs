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
    /// <summary>Creates and validates an occluded runtime emissive source in TestGI.</summary>
    public static class RadiancePhase7SceneSetup
    {
        private const string ScenePath = "Assets/Scenes/TestGI.unity";
        private const string RootName = "Dynamic GI Phase 7 - Emissive Test";
        private const string MaterialFolder = "Assets/Scenes/TestGI/DynamicGI Phase7 Materials";
        private const int GeometryLayer = 30;
        private static readonly Color DefaultEmission = new(1f, 0.025f, 0.65f, 1f);

        [MenuItem("Tools/Dynamic GI/Phase 7/Configure TestGI Emissive Injection")]
        public static void ConfigureTestGI()
        {
            RadiancePhase6SceneSetup.ConfigureTestGI();
            Scene scene = SceneManager.GetActiveScene();
            DestroyGeneratedRoot(scene, RootName);

            Material emissiveMaterial = GetOrCreateEmissiveMaterial("Neon Magenta", DefaultEmission, 4f);
            Material occluderMaterial = GetOrCreateLitMaterial("Emissive Occluder", new Color(0.08f, 0.1f, 0.14f));
            GameObject root = new(RootName) { layer = GeometryLayer };
            SceneManager.MoveGameObjectToScene(root, scene);

            GameObject neon = CreateBlock(
                root.transform,
                "Runtime Neon Sign",
                new Vector3(2.5f, 2f, -3.6f),
                new Vector3(1.5f, 0.5f, 0.1f),
                emissiveMaterial);
            GIEmissiveContributor contributor = neon.AddComponent<GIEmissiveContributor>();
            contributor.Configure(
                new[] { neon.GetComponent<Renderer>() },
                DefaultEmission,
                2.5f,
                4f,
                1,
                true);

            GameObject occluder = CreateBlock(
                root.transform,
                "Emissive DDA Occluder",
                new Vector3(2.5f, 2f, -2.5f),
                new Vector3(2f, 3f, 0.25f),
                occluderMaterial);
            Transform visible = CreateMarker(root.transform, "Emissive Visible Probe Marker", new Vector3(2.5f, 2f, -2.9f));
            Transform blocked = CreateMarker(root.transform, "Emissive Blocked Probe Marker", new Vector3(2.5f, 2f, -1.5f));

            Phase7EmissiveTestGuide guide = root.AddComponent<Phase7EmissiveTestGuide>();
            SerializedObject serializedGuide = new(guide);
            Set(serializedGuide, "contributor", contributor);
            Set(serializedGuide, "visibleProbeMarker", visible);
            Set(serializedGuide, "blockedProbeMarker", blocked);
            Set(serializedGuide, "occluder", occluder.transform);
            serializedGuide.ApplyModifiedPropertiesWithoutUndo();

            WorldRadianceClipmap clipmap = FindSingle<WorldRadianceClipmap>(scene);
            RadianceClipmapDebug fieldDebug = FindSingle<RadianceClipmapDebug>(scene);
            SerializedObject serializedDebug = new(fieldDebug);
            Set(serializedDebug, "probeQueryTarget", visible);
            Set(serializedDebug, "displayedDirection", (int)RadianceDebugDirection.NegativeZ);
            serializedDebug.ApplyModifiedPropertiesWithoutUndo();

            EditorUtility.SetDirty(root);
            EditorUtility.SetDirty(fieldDebug);
            EditorUtility.SetDirty(clipmap);
            EditorSceneManager.MarkSceneDirty(scene);
            if (!EditorSceneManager.SaveScene(scene))
                throw new InvalidOperationException($"Could not save {ScenePath}.");

            Debug.Log(
                "DYNAMIC_GI_PHASE7_TESTGI_CONFIGURED | neon=(2.5,2,-3.6) magenta | range=4m | maxCascade=C1 | " +
                "visibleProbe=(2.5,2,-2.9) | blockedProbe=(2.5,2,-1.5) | debug=-Z/query+ground+ceiling");
        }

        [MenuItem("Tools/Dynamic GI/Phase 7/Validate TestGI Emissive Injection")]
        public static void ValidateTestGI()
        {
            Scene scene = EditorSceneManager.OpenScene(ScenePath, OpenSceneMode.Single);
            WorldGeometryField geometry = FindSingle<WorldGeometryField>(scene);
            WorldSkyVisibilityField sky = FindSingle<WorldSkyVisibilityField>(scene);
            WorldRadianceClipmap clipmap = FindSingle<WorldRadianceClipmap>(scene);
            GIEmissiveContributor contributor = FindSingle<GIEmissiveContributor>(scene);
            Light sun = FindDirectionalSun(scene);
            Transform visible = FindNamedTransform(scene, "Emissive Visible Probe Marker");
            Transform blocked = FindNamedTransform(scene, "Emissive Blocked Probe Marker");

            bool originalSunEnabled = sun.enabled;
            Color originalColor = contributor.EmissionColor;
            float originalIntensity = contributor.EmissionIntensity;
            float originalRange = contributor.InfluenceRange;
            int originalCascade = contributor.MaximumCascadeIndex;
            bool originalContribution = contributor.Contributes;

            try
            {
                sun.enabled = false;
                contributor.Configure(
                    ToRendererArray(contributor.Renderers),
                    DefaultEmission,
                    2.5f,
                    4f,
                    1,
                    true);
                geometry.RebuildAll();
                geometry.ProcessAllDirtyNow();
                sky.RebuildAll();
                sky.ProcessAllDirtyNow();
                clipmap.ForceLightingRefresh();
                clipmap.ProcessAllDirtyNow();

                Vector4 visibleOn = Query(clipmap, visible.position).Probe.Radiance.NegativeZ;
                Vector4 blockedOn = Query(clipmap, blocked.position).Probe.Radiance.NegativeZ;
                float visibleEnergy = Luminance(visibleOn);
                float blockedEnergy = Luminance(blockedOn);
                if (visibleOn.x < visibleOn.y + 0.35f || visibleOn.z < visibleOn.y + 0.12f)
                    throw new InvalidOperationException($"Visible probe did not receive magenta emission: {visibleOn}.");
                if (visibleEnergy < blockedEnergy + 0.08f)
                {
                    throw new InvalidOperationException(
                        $"Emissive DDA wall did not separate probes: visible={visibleEnergy:0.000}, blocked={blockedEnergy:0.000}.");
                }

                int revision = clipmap.EmissiveRevision;
                contributor.SetContributionEnabled(false);
                RadianceClipmapStats invalidated = clipmap.Stats;
                int totalTiles = GetTotalTileCount(clipmap);
                if (clipmap.EmissiveRevision <= revision || invalidated.DirtyTiles <= 0 || invalidated.DirtyTiles >= totalTiles)
                {
                    throw new InvalidOperationException(
                        $"Emissive disable was not locally invalidated: dirty={invalidated.DirtyTiles}/{totalTiles}, " +
                        $"revision={revision}->{clipmap.EmissiveRevision}.");
                }
                clipmap.ProcessAllDirtyNow();
                Vector4 visibleOff = Query(clipmap, visible.position).Probe.Radiance.NegativeZ;
                if (Luminance(visibleOff) > visibleEnergy - 0.08f)
                    throw new InvalidOperationException($"Disabling the contributor did not remove its radiance: {visibleOn} -> {visibleOff}.");

                contributor.Configure(
                    ToRendererArray(contributor.Renderers),
                    new Color(0.02f, 0.75f, 1f, 1f),
                    2.5f,
                    4f,
                    1,
                    true);
                clipmap.ProcessAllDirtyNow();
                Vector4 cyan = Query(clipmap, visible.position).Probe.Radiance.NegativeZ;
                if (cyan.y < cyan.x + 0.25f || cyan.z < cyan.x + 0.35f)
                    throw new InvalidOperationException($"Runtime emissive color update was not uploaded: {cyan}.");

                RadianceClipmapStats stats = clipmap.Stats;
                if (stats.ActiveEmissiveContributors != 1)
                    throw new InvalidOperationException($"Expected one uploaded emissive, found {stats.ActiveEmissiveContributors}.");
                Debug.Log(
                    $"DYNAMIC_GI_PHASE7_VALIDATION_PASSED | visibleMagenta={FormatRgb(visibleOn)} L={visibleEnergy:0.000} | " +
                    $"blocked={FormatRgb(blockedOn)} L={blockedEnergy:0.000} | off={FormatRgb(visibleOff)} | " +
                    $"cyan={FormatRgb(cyan)} | localDirty={invalidated.DirtyTiles}/{totalTiles} | " +
                    $"emissives={stats.ActiveEmissiveContributors} | revision={stats.EmissiveRevision} | " +
                    $"GPU={EditorUtility.FormatBytes(stats.EstimatedGpuBytes)}");
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
                if (clipmap != null && clipmap.IsInitialized)
                {
                    clipmap.ForceLightingRefresh();
                    clipmap.ProcessAllDirtyNow();
                }
            }
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

        private static Material GetOrCreateEmissiveMaterial(string name, Color color, float intensity)
        {
            Material material = GetOrCreateLitMaterial(name, color * 0.08f);
            Color hdr = color * intensity;
            if (material.HasProperty("_EmissiveColor")) material.SetColor("_EmissiveColor", hdr);
            if (material.HasProperty("_EmissiveColorLDR")) material.SetColor("_EmissiveColorLDR", color);
            if (material.HasProperty("_UseEmissiveIntensity")) material.SetFloat("_UseEmissiveIntensity", 0f);
            if (material.HasProperty("_EmissiveExposureWeight")) material.SetFloat("_EmissiveExposureWeight", 0f);
            EditorUtility.SetDirty(material);
            return material;
        }

        private static Material GetOrCreateLitMaterial(string name, Color color)
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
            if (material.HasProperty("_Smoothness")) material.SetFloat("_Smoothness", 0.25f);
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
                if (roots[i].name == name) UnityEngine.Object.DestroyImmediate(roots[i]);
        }

        private static RadianceClipmapProbeResult Query(WorldRadianceClipmap field, Vector3 position)
        {
            RadianceClipmapProbeResult result = default;
            bool complete = false;
            if (!field.RequestProbe(position, value => { result = value; complete = true; }))
                throw new InvalidOperationException("Phase 7 clipmap rejected a GPU query.");
            AsyncGPUReadback.WaitAllRequests();
            if (!complete || result.Probe.HasError)
                throw new InvalidOperationException("Phase 7 GPU radiance query failed.");
            return result;
        }

        private static int GetTotalTileCount(WorldRadianceClipmap clipmap)
        {
            List<RadianceCascadeRuntimeStats> values = new();
            clipmap.GetCascadeStats(values);
            int total = 0;
            for (int i = 0; i < values.Count; i++) total += values[i].TotalTiles;
            return total;
        }

        private static Renderer[] ToRendererArray(IReadOnlyList<Renderer> values)
        {
            Renderer[] result = new Renderer[values.Count];
            for (int i = 0; i < result.Length; i++) result[i] = values[i];
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
        private static string FormatRgb(Vector4 value) => $"({value.x:0.000},{value.y:0.000},{value.z:0.000})";
        private static void Set(SerializedObject target, string name, int value) => target.FindProperty(name).intValue = value;
        private static void Set(SerializedObject target, string name, UnityEngine.Object value) => target.FindProperty(name).objectReferenceValue = value;
    }
}
