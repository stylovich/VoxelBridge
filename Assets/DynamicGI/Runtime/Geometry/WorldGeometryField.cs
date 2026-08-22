using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using DynamicGI.Contributors;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Rendering;

namespace DynamicGI.Geometry
{
    /// <summary>
    /// Sparse, world-space surface occupancy field. Geometry is grouped into CPU-managed
    /// bricks while bit-packed base/dynamic occupancy remains resident on the GPU.
    /// </summary>
    [ExecuteAlways]
    [DefaultExecutionOrder(-500)]
    [DisallowMultipleComponent]
    public sealed class WorldGeometryField : MonoBehaviour
    {
        private const string DefaultComputePath = "Assets/DynamicGI/Shaders/GeometryVoxelize.compute";
        private static readonly ProfilerMarker RebuildMarker = new("DynamicGI.GeometryField.RebuildBricks");

        private static readonly int BaseOccupancyId = Shader.PropertyToID("_DynamicGI_BaseOccupancy");
        private static readonly int DynamicOccupancyId = Shader.PropertyToID("_DynamicGI_DynamicOccupancy");
        private static readonly int PageTableId = Shader.PropertyToID("_DynamicGI_GeometryPageTable");
        private static readonly int ActiveBricksId = Shader.PropertyToID("_DynamicGI_ActiveBricks");
        private static readonly int FieldOriginId = Shader.PropertyToID("_DynamicGI_GeometryFieldOrigin");
        private static readonly int FieldSizeId = Shader.PropertyToID("_DynamicGI_GeometryFieldSize");
        private static readonly int VoxelSizeId = Shader.PropertyToID("_DynamicGI_GeometryVoxelSize");
        private static readonly int BrickWorldSizeId = Shader.PropertyToID("_DynamicGI_GeometryBrickWorldSize");
        private static readonly int BrickResolutionId = Shader.PropertyToID("_DynamicGI_GeometryBrickResolution");
        private static readonly int WordsPerBrickId = Shader.PropertyToID("_DynamicGI_GeometryWordsPerBrick");
        private static readonly int PageTableCapacityId = Shader.PropertyToID("_DynamicGI_GeometryPageTableCapacity");
        private static readonly int FieldAvailableId = Shader.PropertyToID("_DynamicGI_GeometryFieldAvailable");

        [Header("Field (axis-aligned, world space)")]
        [SerializeField] private Vector3 fieldCenter = new(0f, 16f, 0f);
        [SerializeField] private Vector3 fieldSize = new(128f, 32f, 128f);
        [SerializeField, Min(0.05f)] private float voxelSize = 0.5f;
        [SerializeField, Range(4, 32)] private int brickResolution = 16;
        [SerializeField, Min(1)] private int maxActiveBricks = 4096;

        [Header("Geometry collection")]
        [SerializeField] private GeometryCollectionMode collectionMode = GeometryCollectionMode.ExplicitContributorsAndLayerMask;
        [SerializeField] private LayerMask geometryLayers = ~0;
        [SerializeField] private bool rebuildOnEnable = true;

        [Header("Temporal rebuild")]
        [SerializeField, Min(1)] private int rebuildBudgetBricksPerFrame = 4;
        [SerializeField, Min(1)] private int recentRegionLifetimeFrames = 90;

        [Header("Compute")]
        [SerializeField] private ComputeShader voxelizationShader;

        private readonly Dictionary<Vector3Int, GeometryBrick> bricks = new();
        private readonly Queue<GeometryBrick> dirtyQueue = new();
        private readonly HashSet<GeometryBrick> dirtySet = new();
        private readonly List<GeometrySource> sources = new();
        private readonly HashSet<Renderer> explicitlyRegisteredRenderers = new();
        private readonly Dictionary<Mesh, MeshGpuData> meshGpuData = new();
        private readonly List<RecentRegion> recentRegions = new();
        private readonly Stack<int> freeSlots = new();

        private GraphicsBuffer baseOccupancyBuffer;
        private GraphicsBuffer dynamicOccupancyBuffer;
        private GraphicsBuffer pageTableBuffer;
        private GraphicsBuffer activeBricksBuffer;
        private GraphicsBuffer queryPositionBuffer;
        private GraphicsBuffer queryResultBuffer;

        private BrickGpuEntry[] pageTableUpload;
        private BrickGpuEntry[] activeBrickUpload;
        private readonly Vector3[] singleQueryPosition = new Vector3[1];
        private int wordsPerBrick;
        private int pageTableCapacity;
        private int activeBrickCount;
        private int clearKernel = -1;
        private int voxelizeKernel = -1;
        private int queryKernel = -1;
        private int debugKernel = -1;
        private bool initialized;
        private bool configurationDirty;
        private bool capacityWarningIssued;
        private bool queryPending;
        private Vector3 pendingQueryPosition;
        private Action<GeometryOccupancyResult> pendingQueryCallback;
        private Vector3 lastTransformPosition;

        private int updatedBricksThisFrame;
        private int computeDispatchesThisFrame;
        private double updateCpuMilliseconds;
        private int lastDebugBrickCount;
        private int lastDebugSampleCount;
        private int lastDebugSampleStride;

        public static WorldGeometryField Active { get; private set; }

        /// <summary>
        /// Raised after the brick map is reset and a full geometry rebuild is queued.
        /// Dependent fields should invalidate their complete domain, but wait until
        /// DirtyBrickCount reaches zero before consuming occupancy.
        /// </summary>
        public event Action<Bounds> GeometryFieldReset;

        /// <summary>
        /// Raised after one brick has finished GPU voxelization. Consumers can expand
        /// this region according to their own influence radius and deduplicate updates.
        /// </summary>
        public event Action<Bounds> GeometryRegionRebuilt;

        public Bounds FieldBounds => new(transform.position + fieldCenter, fieldSize);
        public float VoxelSize => voxelSize;
        public int BrickResolution => brickResolution;
        public float BrickWorldSize => voxelSize * brickResolution;
        public int ActiveBrickCount => activeBrickCount;
        public int DirtyBrickCount => dirtySet.Count;
        public bool IsInitialized => initialized;
        public ComputeShader VoxelizationShader => voxelizationShader;
        public int LastDebugBrickCount => lastDebugBrickCount;
        public int LastDebugSampleCount => lastDebugSampleCount;
        public int LastDebugSampleStride => lastDebugSampleStride;
        public Vector3Int FieldVoxelResolution => new(
            Mathf.CeilToInt(fieldSize.x / voxelSize),
            Mathf.CeilToInt(fieldSize.y / voxelSize),
            Mathf.CeilToInt(fieldSize.z / voxelSize));

        public GeometryFieldStats Stats => new(
            activeBrickCount,
            dirtySet.Count,
            updatedBricksThisFrame,
            computeDispatchesThisFrame,
            EstimateGpuBytes(),
            updateCpuMilliseconds);

        private void Reset()
        {
            TryAssignDefaultComputeShader();
        }

        private void OnEnable()
        {
            TryAssignDefaultComputeShader();
            GIGeometryContributor.RegistryChanged += OnContributorChanged;
            lastTransformPosition = transform.position;

            if (Active != null && Active != this)
                UnityEngine.Debug.LogWarning("Multiple WorldGeometryField instances are enabled. Shader globals use the most recently enabled field.", this);

            Active = this;
            Initialize();
        }

        private void OnDisable()
        {
            GIGeometryContributor.RegistryChanged -= OnContributorChanged;
            ReleaseGpuResources();

            if (Active == this)
            {
                Active = null;
                Shader.SetGlobalInt(FieldAvailableId, 0);
            }
        }

        private void OnValidate()
        {
            voxelSize = Mathf.Max(0.05f, voxelSize);
            // Power-of-two bricks allow the very hot GPU occupancy lookup used by
            // every DDA step to replace integer division/modulo with bit operations.
            brickResolution = Mathf.ClosestPowerOfTwo(Mathf.Clamp(brickResolution, 4, 32));
            maxActiveBricks = Mathf.Max(1, maxActiveBricks);
            rebuildBudgetBricksPerFrame = Mathf.Max(1, rebuildBudgetBricksPerFrame);
            recentRegionLifetimeFrames = Mathf.Max(1, recentRegionLifetimeFrames);
            fieldSize.x = Mathf.Max(voxelSize, fieldSize.x);
            fieldSize.y = Mathf.Max(voxelSize, fieldSize.y);
            fieldSize.z = Mathf.Max(voxelSize, fieldSize.z);
            TryAssignDefaultComputeShader();

            if (isActiveAndEnabled)
                configurationDirty = true;
        }

        private void Update()
        {
            if (transform.position != lastTransformPosition)
            {
                lastTransformPosition = transform.position;
                configurationDirty = true;
            }

            if (configurationDirty)
            {
                configurationDirty = false;
                Initialize();
            }

            updatedBricksThisFrame = 0;
            computeDispatchesThisFrame = 0;
            updateCpuMilliseconds = 0.0;

            if (!initialized)
                return;

            long startTimestamp = Stopwatch.GetTimestamp();
            using (RebuildMarker.Auto())
                ProcessDirtyBricks(rebuildBudgetBricksPerFrame);
            long endTimestamp = Stopwatch.GetTimestamp();
            updateCpuMilliseconds = (endTimestamp - startTimestamp) * 1000.0 / Stopwatch.Frequency;

            int currentFrame = Time.frameCount;
            for (int i = recentRegions.Count - 1; i >= 0; i--)
            {
                if (recentRegions[i].ExpiryFrame < currentFrame)
                    recentRegions.RemoveAt(i);
            }
        }

        [ContextMenu("Rebuild Entire Geometry Field")]
        public void RebuildAll()
        {
            if (!EnsureInitialized())
                return;

            CollectGeometrySources();
            bricks.Clear();
            dirtyQueue.Clear();
            dirtySet.Clear();
            recentRegions.Clear();
            freeSlots.Clear();
            capacityWarningIssued = false;

            for (int slot = maxActiveBricks - 1; slot >= 0; slot--)
                freeSlots.Push(slot);

            for (int i = 0; i < sources.Count; i++)
                InvalidateRegionInternal(sources[i].Renderer.bounds);

            RebuildPageTable();
            GeometryFieldReset?.Invoke(FieldBounds);
        }

        /// <summary>
        /// Rebuilds all currently invalidated bricks immediately. Intended for setup,
        /// tests, and editor debugging; normal runtime uses the per-frame budget.
        /// </summary>
        [ContextMenu("Process All Invalidated Bricks")]
        public void ProcessAllDirtyNow()
        {
            if (!EnsureInitialized())
                return;

            while (dirtyQueue.Count > 0)
                ProcessDirtyBricks(Mathf.Max(1, dirtyQueue.Count));
        }

        /// <summary>
        /// Invalidates only bricks touched by the supplied world-space bounds.
        /// The affected bricks are cleared and reconstructed from current contributors.
        /// </summary>
        public void InvalidateRegion(Bounds worldBounds)
        {
            if (!EnsureInitialized())
                return;

            InvalidateRegionInternal(worldBounds);
            RebuildPageTable();
        }

        /// <summary>
        /// Executes a real GPU occupancy lookup and returns it asynchronously. Only one
        /// query may be pending on this lightweight prototype API at a time.
        /// </summary>
        public bool RequestOccupancy(Vector3 worldPosition, Action<GeometryOccupancyResult> callback)
        {
            if (!EnsureInitialized() || queryPending || queryPositionBuffer == null || queryResultBuffer == null)
                return false;

            singleQueryPosition[0] = worldPosition;
            queryPositionBuffer.SetData(singleQueryPosition);
            BindFieldData(voxelizationShader, queryKernel);
            voxelizationShader.SetInt("_QueryCount", 1);
            voxelizationShader.SetBuffer(queryKernel, "_QueryPositions", queryPositionBuffer);
            voxelizationShader.SetBuffer(queryKernel, "_QueryResults", queryResultBuffer);
            voxelizationShader.Dispatch(queryKernel, 1, 1, 1);
            computeDispatchesThisFrame++;

            pendingQueryPosition = worldPosition;
            pendingQueryCallback = callback;
            queryPending = true;
            AsyncGPUReadback.Request(queryResultBuffer, OnOccupancyReadback);
            return true;
        }

        /// <summary>
        /// Binds the sparse occupancy resources and world-space layout to another
        /// compute kernel. This is the supported bridge for occlusion/radiance fields;
        /// callers never need access to the underlying buffers.
        /// </summary>
        public bool BindSamplingResources(ComputeShader targetShader, int kernel)
        {
            if (targetShader == null || !EnsureInitialized())
                return false;

            BindFieldData(targetShader, kernel);
            targetShader.SetInt(FieldAvailableId, 1);
            return true;
        }

        /// <summary>
        /// Builds a capped GPU instance list for GeometryFieldDebug.
        /// mode: 0 occupied, 1 empty, 2 both.
        /// </summary>
        public bool BuildDebugVoxelInstances(
            GraphicsBuffer appendBuffer,
            int maximumInstances,
            int mode,
            Vector3 center,
            float radius)
        {
            lastDebugBrickCount = 0;
            lastDebugSampleCount = 0;
            lastDebugSampleStride = 0;

            if (!initialized || appendBuffer == null || activeBrickCount == 0 || maximumInstances <= 0)
                return false;

            int debugBrickCount = 0;
            float radiusSquared = radius * radius;
            foreach (KeyValuePair<Vector3Int, GeometryBrick> pair in bricks)
            {
                GeometryBrick brick = pair.Value;
                if (radius > 0f && brick.WorldBounds.SqrDistance(center) > radiusSquared)
                    continue;

                Vector3Int coordinate = pair.Key;
                activeBrickUpload[debugBrickCount++] = new BrickGpuEntry(
                    coordinate.x,
                    coordinate.y,
                    coordinate.z,
                    brick.Slot + 1);
            }

            if (debugBrickCount == 0)
                return false;

            activeBricksBuffer.SetData(activeBrickUpload, 0, 0, debugBrickCount);
            long voxelsPerBrick = (long)brickResolution * brickResolution * brickResolution;
            long totalVoxels = voxelsPerBrick * debugBrickCount;
            int stride = Mathf.Max(1, (int)Math.Ceiling(totalVoxels / (double)maximumInstances));
            int sampleCount = Mathf.Min(maximumInstances, (int)Math.Ceiling(totalVoxels / (double)stride));
            if (sampleCount <= 0)
                return false;

            lastDebugBrickCount = debugBrickCount;
            lastDebugSampleCount = sampleCount;
            lastDebugSampleStride = stride;

            appendBuffer.SetCounterValue(0);
            BindFieldData(voxelizationShader, debugKernel);
            voxelizationShader.SetBuffer(debugKernel, ActiveBricksId, activeBricksBuffer);
            voxelizationShader.SetBuffer(debugKernel, "_DebugVoxels", appendBuffer);
            voxelizationShader.SetInt("_ActiveBrickCount", debugBrickCount);
            voxelizationShader.SetInt("_DebugSampleCount", sampleCount);
            voxelizationShader.SetInt("_DebugSampleStride", stride);
            voxelizationShader.SetInt("_DebugMode", Mathf.Clamp(mode, 0, 2));
            voxelizationShader.SetVector("_DebugCenter", center);
            voxelizationShader.SetFloat("_DebugRadius", Mathf.Max(0f, radius));
            voxelizationShader.Dispatch(debugKernel, Mathf.CeilToInt(sampleCount / 64f), 1, 1);
            computeDispatchesThisFrame++;
            return true;
        }

        public void GetActiveBricks(List<GeometryBrick> destination)
        {
            if (destination == null)
                return;

            destination.Clear();
            foreach (GeometryBrick brick in bricks.Values)
                destination.Add(brick);
        }

        public void GetRecentRebuiltRegions(List<Bounds> destination)
        {
            if (destination == null)
                return;

            destination.Clear();
            for (int i = 0; i < recentRegions.Count; i++)
                destination.Add(recentRegions[i].Bounds);
        }

        private bool EnsureInitialized()
        {
            if (!initialized)
                Initialize();
            return initialized;
        }

        private void Initialize()
        {
            brickResolution = Mathf.ClosestPowerOfTwo(Mathf.Clamp(brickResolution, 4, 32));
            ReleaseGpuResources();
            TryAssignDefaultComputeShader();

            if (voxelizationShader == null)
            {
                UnityEngine.Debug.LogError("WorldGeometryField requires GeometryVoxelize.compute.", this);
                return;
            }

            if (!SystemInfo.supportsComputeShaders)
            {
                UnityEngine.Debug.LogError("WorldGeometryField requires compute shader support.", this);
                return;
            }

            try
            {
                clearKernel = voxelizationShader.FindKernel("ClearBrick");
                voxelizeKernel = voxelizationShader.FindKernel("VoxelizeTriangles");
                queryKernel = voxelizationShader.FindKernel("QueryOccupancy");
                debugKernel = voxelizationShader.FindKernel("BuildDebugVoxels");
            }
            catch (Exception exception)
            {
                UnityEngine.Debug.LogError($"WorldGeometryField could not find its compute kernels: {exception.Message}", this);
                return;
            }

            int voxelCountPerBrick = brickResolution * brickResolution * brickResolution;
            wordsPerBrick = (voxelCountPerBrick + 31) / 32;
            pageTableCapacity = Mathf.NextPowerOfTwo(Mathf.Max(16, maxActiveBricks * 2));

            try
            {
                baseOccupancyBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, maxActiveBricks * wordsPerBrick, sizeof(uint));
                dynamicOccupancyBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, maxActiveBricks * wordsPerBrick, sizeof(uint));
                pageTableBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, pageTableCapacity, sizeof(int) * 4);
                activeBricksBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, maxActiveBricks, sizeof(int) * 4);
                queryPositionBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(float) * 3);
                queryResultBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(uint));
                pageTableUpload = new BrickGpuEntry[pageTableCapacity];
                activeBrickUpload = new BrickGpuEntry[maxActiveBricks];
            }
            catch (Exception exception)
            {
                UnityEngine.Debug.LogError($"WorldGeometryField failed to allocate GPU storage: {exception.Message}", this);
                ReleaseGpuResources();
                return;
            }

            initialized = true;
            PublishShaderGlobals();

            if (rebuildOnEnable)
                RebuildAll();
            else
                RebuildPageTable();
        }

        private void CollectGeometrySources()
        {
            sources.Clear();
            explicitlyRegisteredRenderers.Clear();

            if (collectionMode != GeometryCollectionMode.LayerMaskOnly)
            {
                foreach (GIGeometryContributor contributor in GIGeometryContributor.ActiveContributors)
                {
                    if (contributor == null || !contributor.Contributes)
                        continue;

                    IReadOnlyList<Renderer> contributorRenderers = contributor.Renderers;
                    for (int i = 0; i < contributorRenderers.Count; i++)
                    {
                        Renderer renderer = contributorRenderers[i];
                        if (renderer == null)
                            continue;

                        explicitlyRegisteredRenderers.Add(renderer);
                        AddGeometrySource(renderer, contributor.ContributionType);
                    }
                }
            }

            if (collectionMode == GeometryCollectionMode.ExplicitContributorsOnly)
                return;

            MeshRenderer[] sceneRenderers = FindObjectsByType<MeshRenderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
            for (int i = 0; i < sceneRenderers.Length; i++)
            {
                MeshRenderer renderer = sceneRenderers[i];
                if (renderer == null || explicitlyRegisteredRenderers.Contains(renderer))
                    continue;

                if ((geometryLayers.value & (1 << renderer.gameObject.layer)) == 0)
                    continue;

                AddGeometrySource(renderer, GeometryContributionType.Base);
            }
        }

        private void AddGeometrySource(Renderer renderer, GeometryContributionType contributionType)
        {
            if (renderer == null || !renderer.enabled || !renderer.gameObject.activeInHierarchy)
                return;

            if (renderer is not MeshRenderer)
                return;

            MeshFilter meshFilter = renderer.GetComponent<MeshFilter>();
            Mesh mesh = meshFilter != null ? meshFilter.sharedMesh : null;
            if (mesh == null || mesh.subMeshCount == 0 || !FieldBounds.Intersects(renderer.bounds))
                return;

            sources.Add(new GeometrySource(renderer, mesh, contributionType));
        }

        private void OnContributorChanged(GIGeometryContributor contributor, Bounds dirtyBounds)
        {
            if (!initialized)
                return;

            if (contributor != null)
                ReleaseContributorMeshCaches(contributor);

            CollectGeometrySources();
            InvalidateRegionInternal(dirtyBounds);
            RebuildPageTable();
        }

        private void ReleaseContributorMeshCaches(GIGeometryContributor contributor)
        {
            IReadOnlyList<Renderer> renderers = contributor.Renderers;
            for (int i = 0; i < renderers.Count; i++)
            {
                if (renderers[i] is not MeshRenderer meshRenderer)
                    continue;

                MeshFilter filter = meshRenderer.GetComponent<MeshFilter>();
                Mesh mesh = filter != null ? filter.sharedMesh : null;
                if (mesh != null && meshGpuData.Remove(mesh, out MeshGpuData data))
                    data.Dispose();
            }
        }

        private void InvalidateRegionInternal(Bounds worldBounds)
        {
            if (!TryGetBrickRange(worldBounds, out Vector3Int minimum, out Vector3Int maximum))
                return;

            for (int z = minimum.z; z <= maximum.z; z++)
            {
                for (int y = minimum.y; y <= maximum.y; y++)
                {
                    for (int x = minimum.x; x <= maximum.x; x++)
                    {
                        Vector3Int coordinate = new(x, y, z);
                        if (!bricks.TryGetValue(coordinate, out GeometryBrick brick))
                        {
                            if (freeSlots.Count == 0)
                            {
                                if (!capacityWarningIssued)
                                {
                                    capacityWarningIssued = true;
                                    UnityEngine.Debug.LogError($"WorldGeometryField reached maxActiveBricks ({maxActiveBricks}). Increase the capacity or narrow the field/layers.", this);
                                }
                                continue;
                            }

                            brick = new GeometryBrick(coordinate, freeSlots.Pop(), GetBrickBounds(coordinate));
                            bricks.Add(coordinate, brick);
                            dirtySet.Add(brick);
                            dirtyQueue.Enqueue(brick);
                            continue;
                        }

                        if (dirtySet.Add(brick))
                        {
                            brick.IsDirty = true;
                            dirtyQueue.Enqueue(brick);
                        }
                    }
                }
            }
        }

        private bool TryGetBrickRange(Bounds requestedBounds, out Vector3Int minimum, out Vector3Int maximum)
        {
            Bounds fieldBounds = FieldBounds;
            Vector3 clippedMinimum = Vector3.Max(requestedBounds.min, fieldBounds.min);
            Vector3 clippedMaximum = Vector3.Min(requestedBounds.max, fieldBounds.max);
            if (clippedMinimum.x >= clippedMaximum.x || clippedMinimum.y >= clippedMaximum.y || clippedMinimum.z >= clippedMaximum.z)
            {
                minimum = default;
                maximum = default;
                return false;
            }

            float brickSize = BrickWorldSize;
            Vector3 relativeMinimum = clippedMinimum - fieldBounds.min;
            Vector3 relativeMaximum = clippedMaximum - fieldBounds.min - Vector3.one * (voxelSize * 0.0001f);
            minimum = FloorToVector3Int(relativeMinimum / brickSize);
            maximum = FloorToVector3Int(relativeMaximum / brickSize);

            Vector3Int gridMaximum = new(
                Mathf.Max(0, Mathf.CeilToInt(fieldSize.x / brickSize) - 1),
                Mathf.Max(0, Mathf.CeilToInt(fieldSize.y / brickSize) - 1),
                Mathf.Max(0, Mathf.CeilToInt(fieldSize.z / brickSize) - 1));
            minimum = Vector3Int.Max(Vector3Int.zero, Vector3Int.Min(minimum, gridMaximum));
            maximum = Vector3Int.Max(Vector3Int.zero, Vector3Int.Min(maximum, gridMaximum));
            return true;
        }

        private static Vector3Int FloorToVector3Int(Vector3 value)
        {
            return new Vector3Int(Mathf.FloorToInt(value.x), Mathf.FloorToInt(value.y), Mathf.FloorToInt(value.z));
        }

        private Bounds GetBrickBounds(Vector3Int coordinate)
        {
            float brickSize = BrickWorldSize;
            Vector3 minimum = FieldBounds.min + (Vector3)coordinate * brickSize;
            return new Bounds(minimum + Vector3.one * (brickSize * 0.5f), Vector3.one * brickSize);
        }

        private void ProcessDirtyBricks(int budget)
        {
            int processed = 0;
            while (processed < budget && dirtyQueue.Count > 0)
            {
                GeometryBrick brick = dirtyQueue.Dequeue();
                if (!dirtySet.Remove(brick))
                    continue;

                RebuildBrick(brick);
                processed++;
            }
        }

        private void RebuildBrick(GeometryBrick brick)
        {
            BindFieldData(voxelizationShader, clearKernel);
            voxelizationShader.SetInt("_TargetBrickSlot", brick.Slot);
            voxelizationShader.Dispatch(clearKernel, Mathf.CeilToInt(wordsPerBrick / 64f), 1, 1);
            computeDispatchesThisFrame++;

            for (int sourceIndex = 0; sourceIndex < sources.Count; sourceIndex++)
            {
                GeometrySource source = sources[sourceIndex];
                if (source.Renderer == null || !source.Renderer.enabled || !source.Renderer.gameObject.activeInHierarchy)
                    continue;
                if (!brick.WorldBounds.Intersects(source.Renderer.bounds))
                    continue;
                if (!TryGetMeshGpuData(source.Mesh, out MeshGpuData gpuData))
                    continue;

                BindFieldData(voxelizationShader, voxelizeKernel);
                voxelizationShader.SetBuffer(voxelizeKernel, "_VertexBuffer", gpuData.VertexBuffer);
                voxelizationShader.SetBuffer(voxelizeKernel, "_IndexBuffer", gpuData.IndexBuffer);
                voxelizationShader.SetMatrix("_LocalToWorld", source.Renderer.localToWorldMatrix);
                voxelizationShader.SetInts("_TargetBrickCoordinate", brick.Coordinate.x, brick.Coordinate.y, brick.Coordinate.z);
                voxelizationShader.SetInt("_TargetBrickSlot", brick.Slot);
                voxelizationShader.SetInt("_TargetIsDynamic", source.ContributionType == GeometryContributionType.Dynamic ? 1 : 0);
                voxelizationShader.SetInt("_VertexStride", gpuData.VertexStride);
                voxelizationShader.SetInt("_PositionOffset", gpuData.PositionOffset);
                voxelizationShader.SetInt("_IndexFormat", gpuData.IndexFormatBits);

                for (int subMeshIndex = 0; subMeshIndex < source.Mesh.subMeshCount; subMeshIndex++)
                {
                    SubMeshDescriptor subMesh = source.Mesh.GetSubMesh(subMeshIndex);
                    if (subMesh.topology != MeshTopology.Triangles || subMesh.indexCount < 3)
                        continue;

                    int triangleCount = subMesh.indexCount / 3;
                    voxelizationShader.SetInt("_StartIndex", subMesh.indexStart);
                    voxelizationShader.SetInt("_BaseVertex", subMesh.baseVertex);
                    voxelizationShader.SetInt("_TriangleCount", triangleCount);
                    voxelizationShader.Dispatch(voxelizeKernel, Mathf.CeilToInt(triangleCount / 64f), 1, 1);
                    computeDispatchesThisFrame++;
                }
            }

            brick.IsDirty = false;
            brick.LastRebuiltFrame = Time.frameCount;
            updatedBricksThisFrame++;
            recentRegions.Add(new RecentRegion(brick.WorldBounds, Time.frameCount + recentRegionLifetimeFrames));
            if (recentRegions.Count > 256)
                recentRegions.RemoveAt(0);
            GeometryRegionRebuilt?.Invoke(brick.WorldBounds);
        }

        private bool TryGetMeshGpuData(Mesh mesh, out MeshGpuData data)
        {
            if (meshGpuData.TryGetValue(mesh, out data))
                return true;

            if (!MeshGpuData.TryCreate(mesh, out data, out string failureReason))
            {
                UnityEngine.Debug.LogWarning($"Dynamic GI skipped mesh '{mesh.name}': {failureReason}", mesh);
                return false;
            }

            meshGpuData.Add(mesh, data);
            return true;
        }

        private void BindFieldData(ComputeShader shader, int kernel)
        {
            Bounds bounds = FieldBounds;
            shader.SetBuffer(kernel, BaseOccupancyId, baseOccupancyBuffer);
            shader.SetBuffer(kernel, DynamicOccupancyId, dynamicOccupancyBuffer);
            shader.SetBuffer(kernel, PageTableId, pageTableBuffer);
            shader.SetVector(FieldOriginId, bounds.min);
            shader.SetVector(FieldSizeId, bounds.size);
            shader.SetFloat(VoxelSizeId, voxelSize);
            shader.SetFloat(BrickWorldSizeId, BrickWorldSize);
            shader.SetInt(BrickResolutionId, brickResolution);
            shader.SetInt(WordsPerBrickId, wordsPerBrick);
            shader.SetInt(PageTableCapacityId, pageTableCapacity);
        }

        private void RebuildPageTable()
        {
            if (!initialized || pageTableBuffer == null)
                return;

            Array.Clear(pageTableUpload, 0, pageTableUpload.Length);
            Array.Clear(activeBrickUpload, 0, activeBrickUpload.Length);
            int activeIndex = 0;

            foreach (KeyValuePair<Vector3Int, GeometryBrick> pair in bricks)
            {
                Vector3Int coordinate = pair.Key;
                GeometryBrick brick = pair.Value;
                int tableIndex = (int)(HashBrickCoordinate(coordinate) & (uint)(pageTableCapacity - 1));

                while (pageTableUpload[tableIndex].W != 0)
                    tableIndex = (tableIndex + 1) & (pageTableCapacity - 1);

                BrickGpuEntry entry = new(coordinate.x, coordinate.y, coordinate.z, brick.Slot + 1);
                pageTableUpload[tableIndex] = entry;
                activeBrickUpload[activeIndex++] = entry;
            }

            activeBrickCount = activeIndex;
            pageTableBuffer.SetData(pageTableUpload);
            if (activeBrickCount > 0)
                activeBricksBuffer.SetData(activeBrickUpload, 0, 0, activeBrickCount);
            PublishShaderGlobals();
        }

        private static uint HashBrickCoordinate(Vector3Int coordinate)
        {
            unchecked
            {
                uint hash = (uint)coordinate.x * 73856093u;
                hash ^= (uint)coordinate.y * 19349663u;
                hash ^= (uint)coordinate.z * 83492791u;
                hash ^= hash >> 16;
                hash *= 0x7feb352du;
                hash ^= hash >> 15;
                hash *= 0x846ca68bu;
                hash ^= hash >> 16;
                return hash;
            }
        }

        private void PublishShaderGlobals()
        {
            if (!initialized)
                return;

            Bounds bounds = FieldBounds;
            Shader.SetGlobalBuffer(BaseOccupancyId, baseOccupancyBuffer);
            Shader.SetGlobalBuffer(DynamicOccupancyId, dynamicOccupancyBuffer);
            Shader.SetGlobalBuffer(PageTableId, pageTableBuffer);
            Shader.SetGlobalVector(FieldOriginId, bounds.min);
            Shader.SetGlobalVector(FieldSizeId, bounds.size);
            Shader.SetGlobalFloat(VoxelSizeId, voxelSize);
            Shader.SetGlobalFloat(BrickWorldSizeId, BrickWorldSize);
            Shader.SetGlobalInt(BrickResolutionId, brickResolution);
            Shader.SetGlobalInt(WordsPerBrickId, wordsPerBrick);
            Shader.SetGlobalInt(PageTableCapacityId, pageTableCapacity);
            Shader.SetGlobalInt(FieldAvailableId, 1);
        }

        private void OnOccupancyReadback(AsyncGPUReadbackRequest request)
        {
            bool error = request.hasError;
            bool occupied = !error && request.GetData<uint>()[0] != 0u;
            Action<GeometryOccupancyResult> callback = pendingQueryCallback;
            Vector3 position = pendingQueryPosition;
            pendingQueryCallback = null;
            queryPending = false;
            callback?.Invoke(new GeometryOccupancyResult(position, occupied, error));
        }

        private long EstimateGpuBytes()
        {
            long occupancyBytes = (long)maxActiveBricks * wordsPerBrick * sizeof(uint) * 2L;
            long pageBytes = (long)pageTableCapacity * sizeof(int) * 4L;
            long activeBytes = (long)maxActiveBricks * sizeof(int) * 4L;
            return occupancyBytes + pageBytes + activeBytes + sizeof(float) * 3L + sizeof(uint);
        }

        private void ReleaseGpuResources()
        {
            initialized = false;
            queryPending = false;
            pendingQueryCallback = null;
            activeBrickCount = 0;
            lastDebugBrickCount = 0;
            lastDebugSampleCount = 0;
            lastDebugSampleStride = 0;

            foreach (MeshGpuData data in meshGpuData.Values)
                data.Dispose();
            meshGpuData.Clear();

            baseOccupancyBuffer?.Dispose();
            dynamicOccupancyBuffer?.Dispose();
            pageTableBuffer?.Dispose();
            activeBricksBuffer?.Dispose();
            queryPositionBuffer?.Dispose();
            queryResultBuffer?.Dispose();
            baseOccupancyBuffer = null;
            dynamicOccupancyBuffer = null;
            pageTableBuffer = null;
            activeBricksBuffer = null;
            queryPositionBuffer = null;
            queryResultBuffer = null;
            pageTableUpload = null;
            activeBrickUpload = null;
            bricks.Clear();
            dirtyQueue.Clear();
            dirtySet.Clear();
            sources.Clear();
            explicitlyRegisteredRenderers.Clear();
            freeSlots.Clear();
            recentRegions.Clear();
        }

        [Conditional("UNITY_EDITOR")]
        private void TryAssignDefaultComputeShader()
        {
#if UNITY_EDITOR
            if (voxelizationShader == null)
                voxelizationShader = UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>(DefaultComputePath);
#endif
        }

        private sealed class GeometrySource
        {
            public readonly Renderer Renderer;
            public readonly Mesh Mesh;
            public readonly GeometryContributionType ContributionType;

            public GeometrySource(Renderer renderer, Mesh mesh, GeometryContributionType contributionType)
            {
                Renderer = renderer;
                Mesh = mesh;
                ContributionType = contributionType;
            }
        }

        private sealed class MeshGpuData : IDisposable
        {
            public readonly GraphicsBuffer VertexBuffer;
            public readonly GraphicsBuffer IndexBuffer;
            public readonly int VertexStride;
            public readonly int PositionOffset;
            public readonly int IndexFormatBits;

            private MeshGpuData(
                GraphicsBuffer vertexBuffer,
                GraphicsBuffer indexBuffer,
                int vertexStride,
                int positionOffset,
                int indexFormatBits)
            {
                VertexBuffer = vertexBuffer;
                IndexBuffer = indexBuffer;
                VertexStride = vertexStride;
                PositionOffset = positionOffset;
                IndexFormatBits = indexFormatBits;
            }

            public static bool TryCreate(Mesh mesh, out MeshGpuData data, out string failureReason)
            {
                data = null;
                failureReason = null;

                if (mesh == null)
                {
                    failureReason = "mesh reference is null";
                    return false;
                }

                if (!mesh.HasVertexAttribute(VertexAttribute.Position) ||
                    mesh.GetVertexAttributeFormat(VertexAttribute.Position) != VertexAttributeFormat.Float32 ||
                    mesh.GetVertexAttributeDimension(VertexAttribute.Position) < 3)
                {
                    failureReason = "position data must contain at least three Float32 components";
                    return false;
                }

                try
                {
                    mesh.vertexBufferTarget |= GraphicsBuffer.Target.Raw;
                    mesh.indexBufferTarget |= GraphicsBuffer.Target.Raw;
                    int stream = mesh.GetVertexAttributeStream(VertexAttribute.Position);
                    GraphicsBuffer vertexBuffer = mesh.GetVertexBuffer(stream);
                    GraphicsBuffer indexBuffer = mesh.GetIndexBuffer();
                    if (vertexBuffer == null || indexBuffer == null)
                    {
                        vertexBuffer?.Dispose();
                        indexBuffer?.Dispose();
                        failureReason = "Unity did not expose raw mesh buffers";
                        return false;
                    }

                    data = new MeshGpuData(
                        vertexBuffer,
                        indexBuffer,
                        mesh.GetVertexBufferStride(stream),
                        mesh.GetVertexAttributeOffset(VertexAttribute.Position),
                        mesh.indexFormat == IndexFormat.UInt16 ? 16 : 32);
                    return true;
                }
                catch (Exception exception)
                {
                    failureReason = exception.Message;
                    return false;
                }
            }

            public void Dispose()
            {
                VertexBuffer?.Dispose();
                IndexBuffer?.Dispose();
            }
        }

        private readonly struct RecentRegion
        {
            public readonly Bounds Bounds;
            public readonly int ExpiryFrame;

            public RecentRegion(Bounds bounds, int expiryFrame)
            {
                Bounds = bounds;
                ExpiryFrame = expiryFrame;
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private readonly struct BrickGpuEntry
        {
            public readonly int X;
            public readonly int Y;
            public readonly int Z;
            public readonly int W;

            public BrickGpuEntry(int x, int y, int z, int w)
            {
                X = x;
                Y = y;
                Z = z;
                W = w;
            }
        }
    }
}
