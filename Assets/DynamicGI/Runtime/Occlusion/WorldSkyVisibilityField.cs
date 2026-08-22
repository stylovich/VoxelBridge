using System;
using System.Collections.Generic;
using System.Diagnostics;
using DynamicGI.Geometry;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace DynamicGI.Occlusion
{
    /// <summary>
    /// Local scalar sky-accessibility volume derived from WorldGeometryField. The
    /// texture is dense and low resolution for reliable trilinear sampling, while
    /// updates are tiled, deduplicated, and driven by rebuilt geometry regions.
    /// </summary>
    [ExecuteAlways]
    [DefaultExecutionOrder(-400)]
    [DisallowMultipleComponent]
    public sealed class WorldSkyVisibilityField : MonoBehaviour
    {
        private const string DefaultComputePath = "Assets/DynamicGI/Shaders/SkyVisibility.compute";
        private static readonly ProfilerMarker UpdateMarker = new("DynamicGI.SkyVisibility.UpdateTiles");

        private static readonly int SkyTextureId = Shader.PropertyToID("_DynamicGI_SkyVisibilityTexture");
        private static readonly int SkyReadTextureId = Shader.PropertyToID("_SkyVisibilityRead");
        private static readonly int SkyOriginId = Shader.PropertyToID("_DynamicGI_SkyVisibilityOrigin");
        private static readonly int SkySizeId = Shader.PropertyToID("_DynamicGI_SkyVisibilitySize");
        private static readonly int SkyResolutionId = Shader.PropertyToID("_DynamicGI_SkyVisibilityResolution");
        private static readonly int SkyAvailableId = Shader.PropertyToID("_DynamicGI_SkyVisibilityAvailable");
        private static readonly int UpdateOffsetId = Shader.PropertyToID("_SkyUpdateOffset");
        private static readonly int UpdateSizeId = Shader.PropertyToID("_SkyUpdateSize");
        private static readonly int RayCountId = Shader.PropertyToID("_SkyRayCount");
        private static readonly int MaxTraceDistanceId = Shader.PropertyToID("_SkyMaxTraceDistance");
        private static readonly int MaxDdaStepsId = Shader.PropertyToID("_SkyMaxDdaSteps");
        private static readonly int RayOriginBiasId = Shader.PropertyToID("_SkyRayOriginBias");

        [Header("Source")]
        [SerializeField] private WorldGeometryField geometryField;

        [Header("Sky sampling")]
        [SerializeField, Min(0.25f)] private float sampleSpacing = 1f;
        [SerializeField, Range(4, 64)] private int rayCount = 24;
        [SerializeField, Min(1f)] private float maxTraceDistance = 48f;
        [SerializeField, Min(0f)] private float rayOriginBias = 0.08f;

        [Header("Tiled updates")]
        [SerializeField, Range(4, 16)] private int tileResolution = 8;
        [SerializeField, Min(1)] private int updateBudgetTilesPerFrame = 8;
        [SerializeField] private bool rebuildOnEnable = true;
        [SerializeField, Min(1)] private int recentRegionLifetimeFrames = 180;

        [Header("Compute")]
        [SerializeField] private ComputeShader skyVisibilityShader;

        private readonly Queue<Vector3Int> dirtyQueue = new();
        private readonly HashSet<Vector3Int> dirtySet = new();
        private readonly List<RecentRegion> recentRegions = new();
        private readonly Vector3[] singleQueryPosition = new Vector3[1];

        private WorldGeometryField subscribedGeometryField;
        private RenderTexture skyVisibilityTexture;
        private GraphicsBuffer queryPositionBuffer;
        private GraphicsBuffer queryResultBuffer;
        private Vector3Int resolution;
        private Vector3Int tileGridResolution;
        private GraphicsFormat textureFormat;
        private Bounds cachedGeometryBounds;
        private float cachedGeometryVoxelSize;
        private int clearKernel = -1;
        private int computeKernel = -1;
        private int queryKernel = -1;
        private int debugKernel = -1;
        private bool initialized;
        private bool configurationDirty;
        private bool queryPending;
        private Vector3 pendingQueryPosition;
        private Action<SkyVisibilityResult> pendingQueryCallback;
        private int updatedTilesThisFrame;
        private int updatedSamplesThisFrame;
        private int computeDispatchesThisFrame;
        private double updateCpuMilliseconds;
        private int lastDebugSampleCount;
        private int lastDebugSampleStride;

        public static WorldSkyVisibilityField Active { get; private set; }

        public WorldGeometryField GeometryField => geometryField;
        public RenderTexture VisibilityTexture => skyVisibilityTexture;
        public Bounds FieldBounds => geometryField != null ? geometryField.FieldBounds : new Bounds(transform.position, Vector3.zero);
        public Vector3Int Resolution => resolution;
        public float SampleSpacing => sampleSpacing;
        public int RayCount => rayCount;
        public float MaxTraceDistance => maxTraceDistance;
        public int DirtyTileCount => dirtySet.Count;
        public int TotalTileCount => tileGridResolution.x * tileGridResolution.y * tileGridResolution.z;
        public bool IsInitialized => initialized;
        public int LastDebugSampleCount => lastDebugSampleCount;
        public int LastDebugSampleStride => lastDebugSampleStride;

        public SkyVisibilityStats Stats => new(
            resolution,
            TotalTileCount,
            dirtySet.Count,
            updatedTilesThisFrame,
            updatedSamplesThisFrame,
            computeDispatchesThisFrame,
            EstimateGpuBytes(),
            updateCpuMilliseconds);

        private void Reset()
        {
            geometryField = GetComponent<WorldGeometryField>();
            TryAssignDefaultComputeShader();
        }

        private void OnEnable()
        {
            if (geometryField == null)
                geometryField = GetComponent<WorldGeometryField>();
            if (geometryField == null)
                geometryField = WorldGeometryField.Active;

            TryAssignDefaultComputeShader();
            RefreshGeometrySubscription();

            if (Active != null && Active != this)
                UnityEngine.Debug.LogWarning("Multiple WorldSkyVisibilityField instances are enabled. Shader globals use the most recently enabled field.", this);

            Active = this;
            Initialize();
        }

        private void OnDisable()
        {
            UnsubscribeGeometryField();
            ReleaseResources();

            if (Active == this)
            {
                Active = null;
                Shader.SetGlobalInt(SkyAvailableId, 0);
            }
        }

        private void OnValidate()
        {
            sampleSpacing = Mathf.Max(0.25f, sampleSpacing);
            rayCount = Mathf.Clamp(rayCount, 4, 64);
            maxTraceDistance = Mathf.Max(1f, maxTraceDistance);
            rayOriginBias = Mathf.Max(0f, rayOriginBias);
            tileResolution = Mathf.Clamp(tileResolution, 4, 16);
            updateBudgetTilesPerFrame = Mathf.Max(1, updateBudgetTilesPerFrame);
            recentRegionLifetimeFrames = Mathf.Max(1, recentRegionLifetimeFrames);
            TryAssignDefaultComputeShader();

            if (isActiveAndEnabled)
                configurationDirty = true;
        }

        private void Update()
        {
            RefreshGeometrySubscription();
            if (geometryField != null &&
                (geometryField.FieldBounds != cachedGeometryBounds || !Mathf.Approximately(geometryField.VoxelSize, cachedGeometryVoxelSize)))
            {
                configurationDirty = true;
            }

            if (configurationDirty)
            {
                configurationDirty = false;
                Initialize();
            }

            updatedTilesThisFrame = 0;
            updatedSamplesThisFrame = 0;
            computeDispatchesThisFrame = 0;
            updateCpuMilliseconds = 0.0;

            if (!initialized || geometryField == null || !geometryField.IsInitialized)
            {
                Shader.SetGlobalInt(SkyAvailableId, 0);
                return;
            }

            PublishShaderGlobals();
            if (geometryField.DirtyBrickCount == 0)
            {
                long startTimestamp = Stopwatch.GetTimestamp();
                using (UpdateMarker.Auto())
                    ProcessDirtyTiles(updateBudgetTilesPerFrame);
                long endTimestamp = Stopwatch.GetTimestamp();
                updateCpuMilliseconds = (endTimestamp - startTimestamp) * 1000.0 / Stopwatch.Frequency;
            }

            int currentFrame = Time.frameCount;
            for (int i = recentRegions.Count - 1; i >= 0; i--)
            {
                if (recentRegions[i].ExpiryFrame < currentFrame)
                    recentRegions.RemoveAt(i);
            }
        }

        [ContextMenu("Rebuild Entire Sky Visibility Field")]
        public void RebuildAll()
        {
            if (!EnsureInitialized())
                return;

            dirtyQueue.Clear();
            dirtySet.Clear();
            recentRegions.Clear();

            for (int z = 0; z < tileGridResolution.z; z++)
            {
                for (int y = 0; y < tileGridResolution.y; y++)
                {
                    for (int x = 0; x < tileGridResolution.x; x++)
                        EnqueueTile(new Vector3Int(x, y, z));
                }
            }
        }

        /// <summary>
        /// Invalidates sky samples that can see the supplied changed-geometry bounds
        /// along one of the configured upper-hemisphere directions.
        /// </summary>
        public void InvalidateRegion(Bounds changedGeometryBounds)
        {
            if (!EnsureInitialized())
                return;

            InvalidateSampleBounds(CalculateInfluenceBounds(changedGeometryBounds));
        }

        [ContextMenu("Process All Invalidated Sky Tiles")]
        public void ProcessAllDirtyNow()
        {
            if (!EnsureInitialized())
                return;

            if (geometryField != null && geometryField.DirtyBrickCount > 0)
            {
                UnityEngine.Debug.LogWarning("Sky Visibility is waiting for Geometry Field bricks to finish rebuilding.", this);
                return;
            }

            while (dirtyQueue.Count > 0)
                ProcessDirtyTiles(Mathf.Max(1, dirtyQueue.Count));
        }

        public bool RequestVisibility(Vector3 worldPosition, Action<SkyVisibilityResult> callback)
        {
            if (!EnsureInitialized() || queryPending || queryPositionBuffer == null || queryResultBuffer == null)
                return false;

            singleQueryPosition[0] = worldPosition;
            queryPositionBuffer.SetData(singleQueryPosition);
            BindSkyData(skyVisibilityShader, queryKernel);
            skyVisibilityShader.SetTexture(queryKernel, SkyReadTextureId, skyVisibilityTexture);
            skyVisibilityShader.SetBuffer(queryKernel, "_SkyQueryPositions", queryPositionBuffer);
            skyVisibilityShader.SetBuffer(queryKernel, "_SkyQueryResults", queryResultBuffer);
            skyVisibilityShader.SetInt("_SkyQueryCount", 1);
            skyVisibilityShader.Dispatch(queryKernel, 1, 1, 1);
            computeDispatchesThisFrame++;

            pendingQueryPosition = worldPosition;
            pendingQueryCallback = callback;
            queryPending = true;
            AsyncGPUReadback.Request(queryResultBuffer, OnVisibilityReadback);
            return true;
        }

        /// <summary>
        /// Generates scalar visibility samples around a debug center on the GPU.
        /// Each float4 contains absolute position WS and visibility in w.
        /// </summary>
        public bool BuildDebugInstances(
            GraphicsBuffer appendBuffer,
            int maximumInstances,
            Vector3 center,
            float radius)
        {
            lastDebugSampleCount = 0;
            lastDebugSampleStride = 0;
            if (!initialized || appendBuffer == null || maximumInstances <= 0)
                return false;

            Bounds bounds = FieldBounds;
            if (radius > 0f)
            {
                Bounds neighborhood = new(center, Vector3.one * radius * 2f);
                if (!bounds.Intersects(neighborhood))
                    return false;
            }

            Vector3 cellSize = GetCellSize();
            Vector3 radiusVector = Vector3.one * Mathf.Max(0f, radius);
            Vector3 minimumWS = radius > 0f ? center - radiusVector : bounds.min;
            Vector3 maximumWS = radius > 0f ? center + radiusVector : bounds.max;
            Vector3Int minimum = WorldToSampleFloor(Vector3.Max(minimumWS, bounds.min), cellSize);
            Vector3Int maximum = WorldToSampleFloor(Vector3.Min(maximumWS, bounds.max - cellSize * 0.0001f), cellSize);
            minimum = ClampSample(minimum);
            maximum = ClampSample(maximum);
            Vector3Int size = maximum - minimum + Vector3Int.one;
            if (size.x <= 0 || size.y <= 0 || size.z <= 0)
                return false;

            long candidateCount = (long)size.x * size.y * size.z;
            int stride = Mathf.Max(1, (int)Math.Ceiling(candidateCount / (double)maximumInstances));
            int sampleCount = Mathf.Min(maximumInstances, (int)Math.Ceiling(candidateCount / (double)stride));
            if (sampleCount <= 0)
                return false;

            lastDebugSampleCount = sampleCount;
            lastDebugSampleStride = stride;
            appendBuffer.SetCounterValue(0);
            BindSkyData(skyVisibilityShader, debugKernel);
            skyVisibilityShader.SetTexture(debugKernel, SkyReadTextureId, skyVisibilityTexture);
            skyVisibilityShader.SetBuffer(debugKernel, "_SkyDebugSamples", appendBuffer);
            skyVisibilityShader.SetInts("_SkyDebugOffset", minimum.x, minimum.y, minimum.z);
            skyVisibilityShader.SetInts("_SkyDebugSize", size.x, size.y, size.z);
            skyVisibilityShader.SetInt("_SkyDebugSampleCount", sampleCount);
            skyVisibilityShader.SetInt("_SkyDebugSampleStride", stride);
            skyVisibilityShader.SetVector("_SkyDebugCenter", center);
            skyVisibilityShader.SetFloat("_SkyDebugRadius", Mathf.Max(0f, radius));
            skyVisibilityShader.Dispatch(debugKernel, Mathf.CeilToInt(sampleCount / 64f), 1, 1);
            computeDispatchesThisFrame++;
            return true;
        }

        public void GetDirtyTileBounds(List<Bounds> destination)
        {
            if (destination == null)
                return;

            destination.Clear();
            foreach (Vector3Int coordinate in dirtySet)
                destination.Add(GetTileBounds(coordinate));
        }

        public void GetRecentUpdatedRegions(List<Bounds> destination)
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
            configurationDirty = false;
            ReleaseResources();
            TryAssignDefaultComputeShader();
            if (geometryField == null || skyVisibilityShader == null || !SystemInfo.supportsComputeShaders)
                return;

            Bounds bounds = geometryField.FieldBounds;
            resolution = new Vector3Int(
                Mathf.Max(1, Mathf.CeilToInt(bounds.size.x / sampleSpacing)),
                Mathf.Max(1, Mathf.CeilToInt(bounds.size.y / sampleSpacing)),
                Mathf.Max(1, Mathf.CeilToInt(bounds.size.z / sampleSpacing)));
            int maximum3DSize = SystemInfo.maxTexture3DSize;
            if (resolution.x > maximum3DSize || resolution.y > maximum3DSize || resolution.z > maximum3DSize)
            {
                UnityEngine.Debug.LogError(
                    $"Sky Visibility resolution {resolution} exceeds max 3D texture size {maximum3DSize}. Increase Sample Spacing.",
                    this);
                return;
            }

            tileGridResolution = new Vector3Int(
                Mathf.CeilToInt(resolution.x / (float)tileResolution),
                Mathf.CeilToInt(resolution.y / (float)tileResolution),
                Mathf.CeilToInt(resolution.z / (float)tileResolution));
            textureFormat = ChooseTextureFormat();

            try
            {
                clearKernel = skyVisibilityShader.FindKernel("ClearSkyVisibility");
                computeKernel = skyVisibilityShader.FindKernel("ComputeSkyVisibility");
                queryKernel = skyVisibilityShader.FindKernel("QuerySkyVisibility");
                debugKernel = skyVisibilityShader.FindKernel("BuildSkyVisibilityDebug");
                skyVisibilityTexture = CreateTexture(bounds, resolution, textureFormat);
                queryPositionBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(float) * 3);
                queryResultBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(float));
            }
            catch (Exception exception)
            {
                UnityEngine.Debug.LogError($"Could not initialize Sky Visibility: {exception.Message}", this);
                ReleaseResources();
                return;
            }

            cachedGeometryBounds = bounds;
            cachedGeometryVoxelSize = geometryField.VoxelSize;
            initialized = true;
            ClearTextureToOpenSky();
            PublishShaderGlobals();

            if (rebuildOnEnable)
                RebuildAll();
        }

        private static GraphicsFormat ChooseTextureFormat()
        {
            if (SystemInfo.IsFormatSupported(GraphicsFormat.R8_UNorm, FormatUsage.LoadStore))
                return GraphicsFormat.R8_UNorm;
            return GraphicsFormat.R16_SFloat;
        }

        private static RenderTexture CreateTexture(Bounds bounds, Vector3Int textureResolution, GraphicsFormat format)
        {
            RenderTextureDescriptor descriptor = new(textureResolution.x, textureResolution.y)
            {
                dimension = TextureDimension.Tex3D,
                volumeDepth = textureResolution.z,
                graphicsFormat = format,
                depthBufferBits = 0,
                msaaSamples = 1,
                enableRandomWrite = true,
                useMipMap = false,
                autoGenerateMips = false,
                sRGB = false
            };

            RenderTexture texture = new(descriptor)
            {
                name = $"Dynamic GI Sky Visibility {textureResolution.x}x{textureResolution.y}x{textureResolution.z}",
                filterMode = FilterMode.Trilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };
            if (!texture.Create())
                throw new InvalidOperationException($"Unity could not create the {format} Sky Visibility texture for {bounds}.");
            return texture;
        }

        private void ClearTextureToOpenSky()
        {
            BindSkyData(skyVisibilityShader, clearKernel);
            skyVisibilityShader.SetTexture(clearKernel, SkyTextureId, skyVisibilityTexture);
            skyVisibilityShader.Dispatch(
                clearKernel,
                Mathf.CeilToInt(resolution.x / 4f),
                Mathf.CeilToInt(resolution.y / 4f),
                Mathf.CeilToInt(resolution.z / 4f));
            computeDispatchesThisFrame++;
        }

        private void ProcessDirtyTiles(int budget)
        {
            int processed = 0;
            while (processed < budget && dirtyQueue.Count > 0)
            {
                Vector3Int coordinate = dirtyQueue.Dequeue();
                if (!dirtySet.Remove(coordinate))
                    continue;

                DispatchTile(coordinate);
                processed++;
            }
        }

        private void DispatchTile(Vector3Int coordinate)
        {
            Vector3Int offset = coordinate * tileResolution;
            Vector3Int size = new(
                Mathf.Min(tileResolution, resolution.x - offset.x),
                Mathf.Min(tileResolution, resolution.y - offset.y),
                Mathf.Min(tileResolution, resolution.z - offset.z));

            if (!geometryField.BindSamplingResources(skyVisibilityShader, computeKernel))
                return;

            BindSkyData(skyVisibilityShader, computeKernel);
            skyVisibilityShader.SetTexture(computeKernel, SkyTextureId, skyVisibilityTexture);
            skyVisibilityShader.SetInts(UpdateOffsetId, offset.x, offset.y, offset.z);
            skyVisibilityShader.SetInts(UpdateSizeId, size.x, size.y, size.z);
            skyVisibilityShader.SetInt(RayCountId, rayCount);
            skyVisibilityShader.SetFloat(MaxTraceDistanceId, maxTraceDistance);
            skyVisibilityShader.SetFloat(RayOriginBiasId, Mathf.Max(rayOriginBias, geometryField.VoxelSize * 0.1f));
            int ddaSteps = Mathf.Clamp(Mathf.CeilToInt(maxTraceDistance / geometryField.VoxelSize * 1.8f) + 4, 8, 2048);
            skyVisibilityShader.SetInt(MaxDdaStepsId, ddaSteps);
            skyVisibilityShader.Dispatch(
                computeKernel,
                Mathf.CeilToInt(size.x / 4f),
                Mathf.CeilToInt(size.y / 4f),
                Mathf.CeilToInt(size.z / 4f));

            updatedTilesThisFrame++;
            updatedSamplesThisFrame += size.x * size.y * size.z;
            computeDispatchesThisFrame++;
            recentRegions.Add(new RecentRegion(GetTileBounds(coordinate), Time.frameCount + recentRegionLifetimeFrames));
            if (recentRegions.Count > 256)
                recentRegions.RemoveAt(0);
        }

        private void BindSkyData(ComputeShader shader, int kernel)
        {
            Bounds bounds = FieldBounds;
            shader.SetVector(SkyOriginId, bounds.min);
            shader.SetVector(SkySizeId, bounds.size);
            shader.SetInts(SkyResolutionId, resolution.x, resolution.y, resolution.z);
            shader.SetInt(SkyAvailableId, initialized ? 1 : 0);
        }

        private void PublishShaderGlobals()
        {
            if (!initialized || skyVisibilityTexture == null)
                return;

            Bounds bounds = FieldBounds;
            Shader.SetGlobalTexture(SkyTextureId, skyVisibilityTexture);
            Shader.SetGlobalVector(SkyOriginId, bounds.min);
            Shader.SetGlobalVector(SkySizeId, bounds.size);
            Shader.SetGlobalVector(SkyResolutionId, new Vector4(resolution.x, resolution.y, resolution.z, 0f));
            Shader.SetGlobalInt(SkyAvailableId, 1);
        }

        private void OnGeometryFieldReset(Bounds bounds)
        {
            if (!initialized || bounds != cachedGeometryBounds)
            {
                configurationDirty = true;
                return;
            }

            RebuildAll();
        }

        private void OnGeometryRegionRebuilt(Bounds bounds)
        {
            if (initialized && dirtySet.Count < TotalTileCount)
                InvalidateSampleBounds(CalculateInfluenceBounds(bounds));
        }

        private Bounds CalculateInfluenceBounds(Bounds changedBounds)
        {
            // Rays travel upward. Therefore an occluder can affect samples below it
            // and horizontally around it, but not samples substantially above it.
            // This conservative AABB also covers the per-sample azimuth rotation.
            Vector3 minimum = changedBounds.min - new Vector3(maxTraceDistance, maxTraceDistance, maxTraceDistance);
            Vector3 maximum = changedBounds.max + new Vector3(maxTraceDistance, sampleSpacing, maxTraceDistance);
            Bounds influenced = new((minimum + maximum) * 0.5f, maximum - minimum);
            influenced.Expand(Vector3.one * sampleSpacing * 2f);
            return influenced;
        }

        private void InvalidateSampleBounds(Bounds worldBounds)
        {
            Bounds fieldBounds = FieldBounds;
            Vector3 clippedMinimum = Vector3.Max(worldBounds.min, fieldBounds.min);
            Vector3 clippedMaximum = Vector3.Min(worldBounds.max, fieldBounds.max);
            if (clippedMinimum.x >= clippedMaximum.x || clippedMinimum.y >= clippedMaximum.y || clippedMinimum.z >= clippedMaximum.z)
                return;

            Vector3 cellSize = GetCellSize();
            Vector3Int minimum = ClampSample(WorldToSampleFloor(clippedMinimum, cellSize));
            Vector3Int maximum = ClampSample(WorldToSampleFloor(clippedMaximum - cellSize * 0.0001f, cellSize));
            Vector3Int minimumTile = new(minimum.x / tileResolution, minimum.y / tileResolution, minimum.z / tileResolution);
            Vector3Int maximumTile = new(maximum.x / tileResolution, maximum.y / tileResolution, maximum.z / tileResolution);

            for (int z = minimumTile.z; z <= maximumTile.z; z++)
            {
                for (int y = minimumTile.y; y <= maximumTile.y; y++)
                {
                    for (int x = minimumTile.x; x <= maximumTile.x; x++)
                        EnqueueTile(new Vector3Int(x, y, z));
                }
            }
        }

        private void EnqueueTile(Vector3Int coordinate)
        {
            if (dirtySet.Add(coordinate))
                dirtyQueue.Enqueue(coordinate);
        }

        private Vector3Int WorldToSampleFloor(Vector3 position, Vector3 cellSize)
        {
            Vector3 relative = position - FieldBounds.min;
            return new Vector3Int(
                Mathf.FloorToInt(relative.x / cellSize.x),
                Mathf.FloorToInt(relative.y / cellSize.y),
                Mathf.FloorToInt(relative.z / cellSize.z));
        }

        private Vector3Int ClampSample(Vector3Int coordinate)
        {
            return new Vector3Int(
                Mathf.Clamp(coordinate.x, 0, resolution.x - 1),
                Mathf.Clamp(coordinate.y, 0, resolution.y - 1),
                Mathf.Clamp(coordinate.z, 0, resolution.z - 1));
        }

        private Vector3 GetCellSize()
        {
            Vector3 size = FieldBounds.size;
            return new Vector3(size.x / resolution.x, size.y / resolution.y, size.z / resolution.z);
        }

        private Bounds GetTileBounds(Vector3Int coordinate)
        {
            Vector3Int offset = coordinate * tileResolution;
            Vector3Int sizeSamples = new(
                Mathf.Min(tileResolution, resolution.x - offset.x),
                Mathf.Min(tileResolution, resolution.y - offset.y),
                Mathf.Min(tileResolution, resolution.z - offset.z));
            Vector3 cellSize = GetCellSize();
            Vector3 minimum = FieldBounds.min + Vector3.Scale((Vector3)offset, cellSize);
            Vector3 size = Vector3.Scale((Vector3)sizeSamples, cellSize);
            return new Bounds(minimum + size * 0.5f, size);
        }

        private void RefreshGeometrySubscription()
        {
            if (subscribedGeometryField == geometryField)
                return;

            UnsubscribeGeometryField();
            subscribedGeometryField = geometryField;
            if (subscribedGeometryField != null)
            {
                subscribedGeometryField.GeometryFieldReset += OnGeometryFieldReset;
                subscribedGeometryField.GeometryRegionRebuilt += OnGeometryRegionRebuilt;
            }

            configurationDirty = true;
        }

        private void UnsubscribeGeometryField()
        {
            if (subscribedGeometryField == null)
                return;

            subscribedGeometryField.GeometryFieldReset -= OnGeometryFieldReset;
            subscribedGeometryField.GeometryRegionRebuilt -= OnGeometryRegionRebuilt;
            subscribedGeometryField = null;
        }

        private void OnVisibilityReadback(AsyncGPUReadbackRequest request)
        {
            bool error = request.hasError;
            float visibility = 1f;
            if (!error)
            {
                var data = request.GetData<float>();
                error = data.Length == 0;
                if (!error)
                    visibility = data[0];
            }
            Action<SkyVisibilityResult> callback = pendingQueryCallback;
            Vector3 position = pendingQueryPosition;
            pendingQueryCallback = null;
            queryPending = false;
            callback?.Invoke(new SkyVisibilityResult(position, Mathf.Clamp01(visibility), error));
        }

        private long EstimateGpuBytes()
        {
            long texels = (long)resolution.x * resolution.y * resolution.z;
            long bytesPerTexel = textureFormat == GraphicsFormat.R8_UNorm ? 1L : 2L;
            return texels * bytesPerTexel + sizeof(float) * 4L;
        }

        private void ReleaseResources()
        {
            initialized = false;
            queryPending = false;
            pendingQueryCallback = null;
            dirtyQueue.Clear();
            dirtySet.Clear();
            recentRegions.Clear();
            resolution = default;
            tileGridResolution = default;
            lastDebugSampleCount = 0;
            lastDebugSampleStride = 0;

            queryPositionBuffer?.Dispose();
            queryResultBuffer?.Dispose();
            queryPositionBuffer = null;
            queryResultBuffer = null;

            if (skyVisibilityTexture != null)
            {
                skyVisibilityTexture.Release();
                if (Application.isPlaying)
                    Destroy(skyVisibilityTexture);
                else
                    DestroyImmediate(skyVisibilityTexture);
                skyVisibilityTexture = null;
            }
        }

        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        private void TryAssignDefaultComputeShader()
        {
#if UNITY_EDITOR
            if (skyVisibilityShader == null)
                skyVisibilityShader = UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>(DefaultComputePath);
#endif
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
    }
}
