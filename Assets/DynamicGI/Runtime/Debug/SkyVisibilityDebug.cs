using System;
using System.Collections.Generic;
using DynamicGI.Occlusion;
using UnityEngine;
using UnityEngine.Rendering;

namespace DynamicGI.Debugging
{
    /// <summary>
    /// GPU-instanced inspection for the scalar Sky Visibility field. Samples are
    /// intentionally smaller than their cells so gradients and structural boundaries
    /// remain readable instead of forming an opaque volume.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public sealed class SkyVisibilityDebug : MonoBehaviour
    {
        private static readonly int DebugSamplesId = Shader.PropertyToID("_DynamicGISkyDebugSamples");
        private static readonly int DebugSampleScaleId = Shader.PropertyToID("_DynamicGISkyDebugSampleScale");

        [SerializeField] private WorldSkyVisibilityField skyVisibilityField;
        [SerializeField] private Camera targetCamera;

        [Header("Gizmos")]
        [SerializeField] private bool showFieldBounds = true;
        [SerializeField] private bool showDirtyTiles = true;
        [SerializeField] private bool showRecentlyUpdatedTiles = true;
        [SerializeField] private bool showCameraNeighborhood = true;
        [SerializeField] private bool showResolutionAndStats = true;

        [Header("Visibility samples")]
        [SerializeField] private bool showVisibilitySamples = true;
        [Tooltip("When disabled, visibility instances are restricted to the active Scene view and never contaminate gameplay cameras.")]
        [SerializeField] private bool renderInstancesInGameView;
        [SerializeField, Min(0f)] private float cameraRadius = 8f;
        [SerializeField, Range(256, 262144)] private int maximumSampleInstances = 32768;
        [SerializeField, Range(0.05f, 0.8f)] private float sampleScale = 0.22f;
        [SerializeField, Range(0.25f, 4f)] private float contrast = 1f;
        [SerializeField] private Color enclosedColor = new(0.9f, 0.05f, 0.25f, 0.7f);
        [SerializeField] private Color openColor = new(0.15f, 0.85f, 1f, 0.18f);

        [Header("GPU query probe")]
        [SerializeField] private bool queryVisibilityAtCamera;
        [SerializeField, Min(0.05f)] private float queryIntervalSeconds = 0.25f;

        private readonly List<Bounds> dirtyTileScratch = new();
        private readonly List<Bounds> recentTileScratch = new();
        private readonly IndirectArguments[] indirectArguments = new IndirectArguments[1];
        private GraphicsBuffer debugSampleBuffer;
        private GraphicsBuffer indirectArgumentsBuffer;
        private Material debugMaterial;
        private Mesh cubeMesh;
        private bool resourcesDirty;
        private bool gpuQueryPending;
        private bool hasGpuQueryResult;
        private float lastVisibility;
        private bool lastQueryHadError;
        private Vector3 lastQueryPosition;
        private double nextQueryTime;

        public WorldSkyVisibilityField SkyVisibilityField => skyVisibilityField;
        public bool HasGpuQueryResult => hasGpuQueryResult;
        public float LastVisibility => lastVisibility;
        public bool LastQueryHadError => lastQueryHadError;
        public Vector3 LastQueryPosition => lastQueryPosition;

        private void Reset()
        {
            skyVisibilityField = GetComponent<WorldSkyVisibilityField>();
        }

        private void OnEnable()
        {
            if (skyVisibilityField == null)
                skyVisibilityField = GetComponent<WorldSkyVisibilityField>();
            CreateResources();
        }

        private void OnDisable()
        {
            ReleaseResources();
        }

        private void OnValidate()
        {
            cameraRadius = Mathf.Max(0f, cameraRadius);
            maximumSampleInstances = Mathf.Clamp(maximumSampleInstances, 256, 262144);
            sampleScale = Mathf.Clamp(sampleScale, 0.05f, 0.8f);
            contrast = Mathf.Clamp(contrast, 0.25f, 4f);
            queryIntervalSeconds = Mathf.Max(0.05f, queryIntervalSeconds);
            resourcesDirty = true;
        }

        private void LateUpdate()
        {
            if (resourcesDirty)
            {
                resourcesDirty = false;
                ReleaseResources();
                CreateResources();
            }

            if (skyVisibilityField == null || !skyVisibilityField.IsInitialized)
                return;

            Vector3 debugCenter = ResolveDebugCenter();
            UpdateGpuQuery(debugCenter);
            if (!showVisibilitySamples || debugSampleBuffer == null || indirectArgumentsBuffer == null || debugMaterial == null)
                return;
            if (!DynamicGIDebugRenderUtility.TryResolveCamera(renderInstancesInGameView, out Camera debugCamera))
                return;

            if (!skyVisibilityField.BuildDebugInstances(debugSampleBuffer, maximumSampleInstances, debugCenter, cameraRadius))
                return;

            GraphicsBuffer.CopyCount(debugSampleBuffer, indirectArgumentsBuffer, sizeof(uint));
            debugMaterial.SetBuffer(DebugSamplesId, debugSampleBuffer);
            debugMaterial.SetFloat(DebugSampleScaleId, skyVisibilityField.SampleSpacing * sampleScale);
            debugMaterial.SetFloat("_Contrast", contrast);
            debugMaterial.SetColor("_EnclosedColor", enclosedColor);
            debugMaterial.SetColor("_OpenColor", openColor);

#pragma warning disable 618
            Graphics.DrawMeshInstancedIndirect(
                cubeMesh,
                0,
                debugMaterial,
                skyVisibilityField.FieldBounds,
                indirectArgumentsBuffer,
                0,
                null,
                ShadowCastingMode.Off,
                false,
                gameObject.layer,
                debugCamera,
                LightProbeUsage.Off);
#pragma warning restore 618
        }

        private void UpdateGpuQuery(Vector3 debugCenter)
        {
            if (!queryVisibilityAtCamera || gpuQueryPending || Time.realtimeSinceStartupAsDouble < nextQueryTime)
                return;

            nextQueryTime = Time.realtimeSinceStartupAsDouble + queryIntervalSeconds;
            gpuQueryPending = skyVisibilityField.RequestVisibility(debugCenter, OnGpuQueryCompleted);
        }

        private void OnGpuQueryCompleted(SkyVisibilityResult result)
        {
            gpuQueryPending = false;
            hasGpuQueryResult = true;
            lastQueryPosition = result.WorldPosition;
            lastVisibility = result.Visibility;
            lastQueryHadError = result.HasError;
        }

        private Vector3 ResolveDebugCenter()
        {
            if (targetCamera != null)
                return targetCamera.transform.position;

            if (Application.isPlaying && Camera.main != null)
                return Camera.main.transform.position;

#if UNITY_EDITOR
            UnityEditor.SceneView sceneView = UnityEditor.SceneView.lastActiveSceneView;
            if (sceneView != null && sceneView.camera != null)
                return sceneView.camera.transform.position;
#endif

            return transform.position;
        }

        private void CreateResources()
        {
            if (debugSampleBuffer != null)
                return;

            Shader shader = Shader.Find("Hidden/DynamicGI/SkyVisibilityDebug");
            if (shader == null)
                return;

            cubeMesh = CreateCubeMesh();
            debugMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            debugSampleBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Append, maximumSampleInstances, sizeof(float) * 4);
            indirectArgumentsBuffer = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, 1, sizeof(uint) * 5);
            indirectArguments[0] = new IndirectArguments
            {
                IndexCountPerInstance = cubeMesh.GetIndexCount(0),
                InstanceCount = 0,
                StartIndex = cubeMesh.GetIndexStart(0),
                BaseVertex = cubeMesh.GetBaseVertex(0),
                StartInstance = 0
            };
            indirectArgumentsBuffer.SetData(indirectArguments);
        }

        private void ReleaseResources()
        {
            debugSampleBuffer?.Dispose();
            indirectArgumentsBuffer?.Dispose();
            debugSampleBuffer = null;
            indirectArgumentsBuffer = null;

            if (debugMaterial != null)
            {
                if (Application.isPlaying)
                    Destroy(debugMaterial);
                else
                    DestroyImmediate(debugMaterial);
                debugMaterial = null;
            }

            if (cubeMesh != null)
            {
                if (Application.isPlaying)
                    Destroy(cubeMesh);
                else
                    DestroyImmediate(cubeMesh);
                cubeMesh = null;
            }
        }

        private void OnDrawGizmos()
        {
            if (skyVisibilityField == null)
                skyVisibilityField = GetComponent<WorldSkyVisibilityField>();
            if (skyVisibilityField == null)
                return;

            if (showFieldBounds)
            {
                Gizmos.color = new Color(0.65f, 0.25f, 1f, 0.9f);
                Gizmos.DrawWireCube(skyVisibilityField.FieldBounds.center, skyVisibilityField.FieldBounds.size);
            }

            if (showDirtyTiles)
            {
                skyVisibilityField.GetDirtyTileBounds(dirtyTileScratch);
                Gizmos.color = new Color(1f, 0.1f, 0.15f, 0.65f);
                for (int i = 0; i < dirtyTileScratch.Count; i++)
                    Gizmos.DrawWireCube(dirtyTileScratch[i].center, dirtyTileScratch[i].size * 0.96f);
            }

            if (showRecentlyUpdatedTiles)
            {
                skyVisibilityField.GetRecentUpdatedRegions(recentTileScratch);
                Gizmos.color = new Color(1f, 0.7f, 0.05f, 0.75f);
                for (int i = 0; i < recentTileScratch.Count; i++)
                    Gizmos.DrawWireCube(recentTileScratch[i].center, recentTileScratch[i].size * 0.9f);
            }

            Vector3 debugCenter = ResolveDebugCenter();
            if (showCameraNeighborhood && cameraRadius > 0f)
            {
                Gizmos.color = new Color(0.2f, 0.9f, 1f, 0.7f);
                Gizmos.DrawWireSphere(debugCenter, cameraRadius);
            }

            if (queryVisibilityAtCamera && hasGpuQueryResult)
            {
                Gizmos.color = lastQueryHadError ? Color.red : Color.Lerp(enclosedColor, openColor, lastVisibility);
                Gizmos.DrawWireSphere(lastQueryPosition, skyVisibilityField.SampleSpacing * 0.4f);
            }

#if UNITY_EDITOR
            if (showResolutionAndStats)
            {
                SkyVisibilityStats stats = skyVisibilityField.Stats;
                string query = queryVisibilityAtCamera
                    ? $"\nGPU query: {(!hasGpuQueryResult ? "pending" : lastQueryHadError ? "error" : lastVisibility.ToString("0.000"))}"
                    : string.Empty;
                UnityEditor.Handles.color = Color.white;
                UnityEditor.Handles.Label(
                    skyVisibilityField.FieldBounds.min,
                    $"Sky Visibility\nresolution {stats.Resolution.x} x {stats.Resolution.y} x {stats.Resolution.z} | " +
                    $"{skyVisibilityField.RayCount} rays | {skyVisibilityField.SampleSpacing:0.##} m\n" +
                    $"tiles dirty {stats.DirtyTiles}/{stats.TotalTiles} | updated {stats.UpdatedTilesThisFrame}\n" +
                    $"samples {stats.UpdatedSamplesThisFrame} | debug {skyVisibilityField.LastDebugSampleCount} / stride {skyVisibilityField.LastDebugSampleStride}\n" +
                    $"dispatches {stats.ComputeDispatchesThisFrame} | GPU ~{FormatBytes(stats.EstimatedGpuBytes)} | CPU {stats.UpdateCpuMilliseconds:0.###} ms{query}");
            }
#endif
        }

        private static Mesh CreateCubeMesh()
        {
            Mesh mesh = new() { name = "Dynamic GI Sky Debug Sample", hideFlags = HideFlags.HideAndDontSave };
            mesh.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(0.5f, -0.5f, -0.5f),
                new Vector3(0.5f, 0.5f, -0.5f), new Vector3(-0.5f, 0.5f, -0.5f),
                new Vector3(-0.5f, -0.5f, 0.5f), new Vector3(0.5f, -0.5f, 0.5f),
                new Vector3(0.5f, 0.5f, 0.5f), new Vector3(-0.5f, 0.5f, 0.5f)
            };
            mesh.triangles = new[]
            {
                0, 2, 1, 0, 3, 2,
                1, 2, 6, 1, 6, 5,
                5, 6, 7, 5, 7, 4,
                4, 7, 3, 4, 3, 0,
                3, 7, 6, 3, 6, 2,
                4, 0, 1, 4, 1, 5
            };
            mesh.RecalculateBounds();
            mesh.UploadMeshData(true);
            return mesh;
        }

#if UNITY_EDITOR
        private static string FormatBytes(long bytes)
        {
            if (bytes >= 1024L * 1024L)
                return $"{bytes / (1024f * 1024f):0.0} MiB";
            if (bytes >= 1024L)
                return $"{bytes / 1024f:0.0} KiB";
            return $"{bytes} B";
        }
#endif

        private struct IndirectArguments
        {
            public uint IndexCountPerInstance;
            public uint InstanceCount;
            public uint StartIndex;
            public uint BaseVertex;
            public uint StartInstance;
        }
    }
}
