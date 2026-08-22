using System;
using System.Collections.Generic;
using DynamicGI.Geometry;
using UnityEngine;
using UnityEngine.Rendering;

namespace DynamicGI.Debugging
{
    /// <summary>
    /// Inspection layer for the sparse geometry field. Voxel cubes are generated and
    /// instanced entirely on the GPU; bounds and rebuild history use scene gizmos.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public sealed class GeometryFieldDebug : MonoBehaviour
    {
        private static readonly int DebugVoxelsId = Shader.PropertyToID("_DynamicGIDebugVoxels");
        private static readonly int DebugVoxelScaleId = Shader.PropertyToID("_DynamicGIDebugVoxelScale");

        [SerializeField] private WorldGeometryField geometryField;
        [SerializeField] private Camera targetCamera;

        [Header("Gizmos")]
        [SerializeField] private bool showFieldBounds = true;
        [SerializeField] private bool showActiveBricks = true;
        [SerializeField] private bool showRecentlyRebuiltRegions = true;
        [SerializeField] private bool showCameraNeighborhood = true;
        [SerializeField] private bool showResolutionAndStats = true;

        [Header("Voxel instancing")]
        [SerializeField] private bool showOccupiedVoxels = true;
        [SerializeField] private bool showEmptyVoxels;
        [SerializeField, Min(0f)] private float cameraRadius = 12f;
        [SerializeField, Range(256, 262144)] private int maximumVoxelInstances = 32768;
        [SerializeField, Range(0.1f, 1f)] private float voxelScale = 0.88f;
        [SerializeField] private Color occupiedColor = new(0.1f, 1f, 0.35f, 0.65f);
        [SerializeField] private Color emptyColor = new(0.15f, 0.55f, 1f, 0.08f);

        [Header("GPU query probe")]
        [SerializeField] private bool queryOccupancyAtCamera;
        [SerializeField, Min(0.05f)] private float queryIntervalSeconds = 0.25f;

        private readonly List<GeometryBrick> brickScratch = new();
        private readonly List<Bounds> rebuiltRegionScratch = new();
        private readonly IndirectArguments[] indirectArguments = new IndirectArguments[1];
        private GraphicsBuffer debugVoxelBuffer;
        private GraphicsBuffer indirectArgumentsBuffer;
        private Material debugMaterial;
        private Mesh cubeMesh;
        private bool resourcesDirty;
        private bool gpuQueryPending;
        private bool lastGpuQueryOccupied;
        private bool lastGpuQueryHadError;
        private bool hasGpuQueryResult;
        private Vector3 lastGpuQueryPosition;
        private double nextQueryTime;

        public WorldGeometryField GeometryField => geometryField;
        public Camera TargetCamera => targetCamera;
        public bool HasGpuQueryResult => hasGpuQueryResult;
        public bool LastGpuQueryOccupied => lastGpuQueryOccupied;
        public bool LastGpuQueryHadError => lastGpuQueryHadError;
        public Vector3 LastGpuQueryPosition => lastGpuQueryPosition;

        private void Reset()
        {
            geometryField = GetComponent<WorldGeometryField>();
        }

        private void OnEnable()
        {
            if (geometryField == null)
                geometryField = GetComponent<WorldGeometryField>();
            CreateResources();
        }

        private void OnDisable()
        {
            ReleaseResources();
        }

        private void OnValidate()
        {
            maximumVoxelInstances = Mathf.Clamp(maximumVoxelInstances, 256, 262144);
            cameraRadius = Mathf.Max(0f, cameraRadius);
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

            if (geometryField == null || !geometryField.IsInitialized)
                return;

            Vector3 debugCenter = ResolveDebugCenter();
            UpdateGpuQuery(debugCenter);

            if ((!showOccupiedVoxels && !showEmptyVoxels) || debugVoxelBuffer == null || indirectArgumentsBuffer == null || debugMaterial == null)
                return;

            int mode = showOccupiedVoxels && showEmptyVoxels ? 2 : (showOccupiedVoxels ? 0 : 1);
            if (!geometryField.BuildDebugVoxelInstances(debugVoxelBuffer, maximumVoxelInstances, mode, debugCenter, cameraRadius))
                return;

            GraphicsBuffer.CopyCount(debugVoxelBuffer, indirectArgumentsBuffer, sizeof(uint));
            debugMaterial.SetBuffer(DebugVoxelsId, debugVoxelBuffer);
            debugMaterial.SetFloat(DebugVoxelScaleId, geometryField.VoxelSize * voxelScale);
            debugMaterial.SetColor("_OccupiedColor", occupiedColor);
            debugMaterial.SetColor("_EmptyColor", emptyColor);

#pragma warning disable 618
            Graphics.DrawMeshInstancedIndirect(
                cubeMesh,
                0,
                debugMaterial,
                geometryField.FieldBounds,
                indirectArgumentsBuffer,
                0,
                null,
                ShadowCastingMode.Off,
                false,
                gameObject.layer,
                null,
                LightProbeUsage.Off);
#pragma warning restore 618
        }

        private void UpdateGpuQuery(Vector3 debugCenter)
        {
            if (!queryOccupancyAtCamera || gpuQueryPending || Time.realtimeSinceStartupAsDouble < nextQueryTime)
                return;

            nextQueryTime = Time.realtimeSinceStartupAsDouble + queryIntervalSeconds;
            gpuQueryPending = geometryField.RequestOccupancy(debugCenter, OnGpuQueryCompleted);
        }

        private void OnGpuQueryCompleted(GeometryOccupancyResult result)
        {
            gpuQueryPending = false;
            lastGpuQueryPosition = result.WorldPosition;
            lastGpuQueryOccupied = result.IsOccupied;
            lastGpuQueryHadError = result.HasError;
            hasGpuQueryResult = true;
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
            if (debugVoxelBuffer != null)
                return;

            Shader shader = Shader.Find("Hidden/DynamicGI/GeometryFieldDebug");
            if (shader == null)
                return;

            cubeMesh = CreateCubeMesh();
            debugMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            debugVoxelBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Append, maximumVoxelInstances, sizeof(float) * 4);
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
            debugVoxelBuffer?.Dispose();
            indirectArgumentsBuffer?.Dispose();
            debugVoxelBuffer = null;
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

        private static Mesh CreateCubeMesh()
        {
            Mesh mesh = new() { name = "Dynamic GI Debug Voxel", hideFlags = HideFlags.HideAndDontSave };
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

        private void OnDrawGizmos()
        {
            if (geometryField == null)
                geometryField = GetComponent<WorldGeometryField>();
            if (geometryField == null)
                return;

            if (showFieldBounds)
            {
                Gizmos.color = new Color(1f, 0.8f, 0.1f, 0.9f);
                Gizmos.DrawWireCube(geometryField.FieldBounds.center, geometryField.FieldBounds.size);
            }

            if (showActiveBricks)
            {
                geometryField.GetActiveBricks(brickScratch);
                Gizmos.color = new Color(0.1f, 0.65f, 1f, 0.28f);
                for (int i = 0; i < brickScratch.Count; i++)
                    Gizmos.DrawWireCube(brickScratch[i].WorldBounds.center, brickScratch[i].WorldBounds.size);
            }

            if (showRecentlyRebuiltRegions)
            {
                geometryField.GetRecentRebuiltRegions(rebuiltRegionScratch);
                Gizmos.color = new Color(1f, 0.15f, 0.75f, 0.9f);
                for (int i = 0; i < rebuiltRegionScratch.Count; i++)
                    Gizmos.DrawWireCube(rebuiltRegionScratch[i].center, rebuiltRegionScratch[i].size * 0.98f);
            }

            Vector3 debugCenter = ResolveDebugCenter();
            if (showCameraNeighborhood && cameraRadius > 0f)
            {
                Gizmos.color = new Color(0.2f, 1f, 0.75f, 0.7f);
                Gizmos.DrawWireSphere(debugCenter, cameraRadius);
            }

            if (queryOccupancyAtCamera && hasGpuQueryResult)
            {
                Gizmos.color = lastGpuQueryHadError
                    ? Color.red
                    : lastGpuQueryOccupied
                        ? new Color(0.1f, 1f, 0.35f, 1f)
                        : new Color(0.15f, 0.55f, 1f, 1f);
                Gizmos.DrawWireCube(lastGpuQueryPosition, Vector3.one * geometryField.VoxelSize * 1.25f);
            }

#if UNITY_EDITOR
            if (showResolutionAndStats)
            {
                GeometryFieldStats stats = geometryField.Stats;
                Vector3Int resolution = geometryField.FieldVoxelResolution;
                string memory = EditorUtilityFormatBytes(stats.EstimatedGpuBytes);
                string query = queryOccupancyAtCamera
                    ? $"\nGPU query: {(!hasGpuQueryResult ? "pending" : lastGpuQueryHadError ? "error" : lastGpuQueryOccupied ? "occupied" : "empty")}"
                    : string.Empty;
                UnityEditor.Handles.color = Color.white;
                UnityEditor.Handles.Label(
                    geometryField.FieldBounds.min,
                    $"Geometry Field\nresolution {resolution.x} x {resolution.y} x {resolution.z}\n" +
                    $"voxel {geometryField.VoxelSize:0.###} m | brick {geometryField.BrickResolution}³\n" +
                    $"active {stats.ActiveBricks} | dirty {stats.DirtyBricks} | updated {stats.UpdatedBricksThisFrame}\n" +
                    $"debug {geometryField.LastDebugBrickCount} bricks | {geometryField.LastDebugSampleCount} samples | stride {geometryField.LastDebugSampleStride}\n" +
                    $"dispatches {stats.ComputeDispatchesThisFrame} | GPU ~{memory} | CPU {stats.UpdateCpuMilliseconds:0.###} ms{query}");
            }
#endif
        }

#if UNITY_EDITOR
        private static string EditorUtilityFormatBytes(long bytes)
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
