using System;
using System.Collections.Generic;
using System.Diagnostics;
using DynamicGI.Geometry;
using DynamicGI.Occlusion;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace DynamicGI.Radiance
{
    /// <summary>
    /// Phase 4 local directional diffuse field. Every probe stores RGB irradiance for
    /// six axis-aligned surface normals. It injects sky accessibility and one realtime
    /// directional light; multi-probe propagation and cascades intentionally come later.
    /// </summary>
    [ExecuteAlways]
    [DefaultExecutionOrder(-300)]
    [DisallowMultipleComponent]
    public sealed class WorldRadianceField : MonoBehaviour
    {
        private const string DefaultComputePath = "Assets/DynamicGI/Shaders/RadianceInject.compute";
        private static readonly ProfilerMarker UpdateMarker = new("DynamicGI.Radiance.UpdateTiles");

        private static readonly int PositiveXId = Shader.PropertyToID("_DynamicGI_RadiancePositiveX");
        private static readonly int NegativeXId = Shader.PropertyToID("_DynamicGI_RadianceNegativeX");
        private static readonly int PositiveYId = Shader.PropertyToID("_DynamicGI_RadiancePositiveY");
        private static readonly int NegativeYId = Shader.PropertyToID("_DynamicGI_RadianceNegativeY");
        private static readonly int PositiveZId = Shader.PropertyToID("_DynamicGI_RadiancePositiveZ");
        private static readonly int NegativeZId = Shader.PropertyToID("_DynamicGI_RadianceNegativeZ");
        private static readonly int OriginId = Shader.PropertyToID("_DynamicGI_RadianceOrigin");
        private static readonly int SizeId = Shader.PropertyToID("_DynamicGI_RadianceSize");
        private static readonly int ResolutionId = Shader.PropertyToID("_DynamicGI_RadianceResolution");
        private static readonly int AvailableId = Shader.PropertyToID("_DynamicGI_RadianceAvailable");
        private static readonly int UpdateOffsetId = Shader.PropertyToID("_RadianceUpdateOffset");
        private static readonly int UpdateSizeId = Shader.PropertyToID("_RadianceUpdateSize");

        [Header("Sources")]
        [SerializeField] private WorldGeometryField geometryField;
        [SerializeField] private WorldSkyVisibilityField skyVisibilityField;
        [SerializeField] private Light sunLight;

        [Header("Local probe volume")]
        [SerializeField] private Vector3 fieldCenter = new(0f, 3f, 0f);
        [SerializeField] private Vector3 fieldSize = new(16f, 8f, 16f);
        [SerializeField, Min(0.25f)] private float probeSpacing = 1f;
        [SerializeField] private bool followTarget = true;
        [SerializeField] private Transform followTargetTransform;

        [Header("Injection")]
        [SerializeField] private Color skyColor = new(0.22f, 0.38f, 0.65f, 1f);
        [SerializeField, Min(0f)] private float skyIntensity = 0.2f;
        [SerializeField, Min(0f)] private float sunIntensityScale = 0.00001f;
        [SerializeField, Min(1f)] private float sunTraceDistance = 64f;
        [SerializeField, Min(0f)] private float rayOriginBias = 0.08f;

        [Header("Tiled updates")]
        [SerializeField, Range(2, 16)] private int tileResolution = 4;
        [SerializeField, Min(1)] private int updateBudgetTilesPerFrame = 8;
        [SerializeField] private bool rebuildOnEnable = true;
        [SerializeField, Min(1)] private int recentRegionLifetimeFrames = 180;

        [Header("Compute")]
        [SerializeField] private ComputeShader radianceShader;

        private readonly RenderTexture[] radianceTextures = new RenderTexture[6];
        private readonly Queue<Vector3Int> dirtyQueue = new();
        private readonly HashSet<Vector3Int> dirtySet = new();
        private readonly List<RecentRegion> recentRegions = new();
        private readonly Vector3[] singleQueryPosition = new Vector3[1];

        private WorldGeometryField subscribedGeometryField;
        private WorldSkyVisibilityField subscribedSkyField;
        private GraphicsBuffer queryPositionBuffer;
        private GraphicsBuffer queryResultBuffer;
        private GraphicsBuffer emptyEmissiveBuffer;
        private Vector3Int resolution;
        private Vector3Int tileGridResolution;
        private Vector3 resolvedOrigin;
        private Vector3 lastFollowOrigin;
        private GraphicsFormat textureFormat;
        private int clearKernel = -1;
        private int injectKernel = -1;
        private int queryKernel = -1;
        private int debugKernel = -1;
        private bool initialized;
        private bool configurationDirty;
        private bool queryPending;
        private Vector3 pendingQueryPosition;
        private Action<RadianceProbeResult> pendingQueryCallback;
        private Vector3 lastSunDirection;
        private Vector3 lastSunRadiance;
        private Vector3 lastSkyRadiance;
        private int updatedTilesThisFrame;
        private int updatedProbesThisFrame;
        private int computeDispatchesThisFrame;
        private double updateCpuMilliseconds;
        private int lastDebugProbeCount;
        private int lastDebugProbeStride;

        public static WorldRadianceField Active { get; private set; }

        public WorldGeometryField GeometryField => geometryField;
        public WorldSkyVisibilityField SkyVisibilityField => skyVisibilityField;
        public Light SunLight => sunLight;
        public Bounds FieldBounds => new(resolvedOrigin + fieldSize * 0.5f, fieldSize);
        public Vector3Int Resolution => resolution;
        public float ProbeSpacing => probeSpacing;
        public int DirtyTileCount => dirtySet.Count;
        public int TotalTileCount => tileGridResolution.x * tileGridResolution.y * tileGridResolution.z;
        public bool IsInitialized => initialized;
        public int LastDebugProbeCount => lastDebugProbeCount;
        public int LastDebugProbeStride => lastDebugProbeStride;
        public IReadOnlyList<RenderTexture> RadianceTextures => radianceTextures;

        public RadianceFieldStats Stats => new(
            resolution,
            resolution.x * resolution.y * resolution.z,
            TotalTileCount,
            dirtySet.Count,
            updatedTilesThisFrame,
            updatedProbesThisFrame,
            computeDispatchesThisFrame,
            EstimateGpuBytes(),
            updateCpuMilliseconds);

        private void Reset()
        {
            geometryField = GetComponent<WorldGeometryField>();
            skyVisibilityField = GetComponent<WorldSkyVisibilityField>();
            TryAssignDefaultComputeShader();
        }

        private void OnEnable()
        {
            geometryField ??= GetComponent<WorldGeometryField>();
            skyVisibilityField ??= GetComponent<WorldSkyVisibilityField>();
            geometryField ??= WorldGeometryField.Active;
            skyVisibilityField ??= WorldSkyVisibilityField.Active;
            TryAssignDefaultComputeShader();
            RefreshSubscriptions();

            if (Active != null && Active != this)
                UnityEngine.Debug.LogWarning("Multiple WorldRadianceField instances are enabled. Shader globals use the most recently enabled field.", this);
            Active = this;
            Initialize();
        }

        private void OnDisable()
        {
            UnsubscribeSources();
            ReleaseResources();
            if (Active == this)
            {
                Active = null;
                Shader.SetGlobalInt(AvailableId, 0);
            }
        }

        private void OnValidate()
        {
            probeSpacing = Mathf.Max(0.25f, probeSpacing);
            fieldSize = Vector3.Max(fieldSize, Vector3.one * probeSpacing);
            skyIntensity = Mathf.Max(0f, skyIntensity);
            sunIntensityScale = Mathf.Max(0f, sunIntensityScale);
            sunTraceDistance = Mathf.Max(1f, sunTraceDistance);
            rayOriginBias = Mathf.Max(0f, rayOriginBias);
            tileResolution = Mathf.Clamp(tileResolution, 2, 16);
            updateBudgetTilesPerFrame = Mathf.Max(1, updateBudgetTilesPerFrame);
            recentRegionLifetimeFrames = Mathf.Max(1, recentRegionLifetimeFrames);
            TryAssignDefaultComputeShader();
            if (isActiveAndEnabled)
                configurationDirty = true;
        }

        private void Update()
        {
            RefreshSubscriptions();
            Vector3 desiredOrigin = CalculateFieldOrigin();
            if (followTarget && desiredOrigin != lastFollowOrigin)
                configurationDirty = true;

            if (configurationDirty)
                Initialize();

            updatedTilesThisFrame = 0;
            updatedProbesThisFrame = 0;
            computeDispatchesThisFrame = 0;
            updateCpuMilliseconds = 0.0;

            if (!initialized || geometryField == null || !geometryField.IsInitialized)
            {
                Shader.SetGlobalInt(AvailableId, 0);
                return;
            }

            DetectLightingChanges();
            PublishShaderGlobals();
            bool skyReady = skyVisibilityField == null ||
                            (skyVisibilityField.IsInitialized && skyVisibilityField.DirtyTileCount == 0);
            if (geometryField.DirtyBrickCount == 0 && skyReady)
            {
                long startTimestamp = Stopwatch.GetTimestamp();
                using (UpdateMarker.Auto())
                    ProcessDirtyTiles(updateBudgetTilesPerFrame);
                updateCpuMilliseconds = (Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency;
            }

            int currentFrame = Time.frameCount;
            for (int i = recentRegions.Count - 1; i >= 0; i--)
            {
                if (recentRegions[i].ExpiryFrame < currentFrame)
                    recentRegions.RemoveAt(i);
            }
        }

        [ContextMenu("Rebuild Entire Radiance Field")]
        public void RebuildAll()
        {
            if (!EnsureInitialized())
                return;
            dirtyQueue.Clear();
            dirtySet.Clear();
            recentRegions.Clear();
            InvalidateAll(false);
        }

        public void InvalidateRegion(Bounds worldBounds)
        {
            if (!EnsureInitialized())
                return;
            EnqueueBounds(worldBounds);
        }

        [ContextMenu("Process All Invalidated Radiance Tiles")]
        public void ProcessAllDirtyNow()
        {
            if (!EnsureInitialized())
                return;
            if (geometryField == null || geometryField.DirtyBrickCount > 0 ||
                (skyVisibilityField != null && skyVisibilityField.DirtyTileCount > 0))
            {
                UnityEngine.Debug.LogWarning("Radiance Field is waiting for Geometry/Sky Visibility updates.", this);
                return;
            }

            while (dirtyQueue.Count > 0)
                ProcessDirtyTiles(Mathf.Max(1, dirtyQueue.Count));
            PublishShaderGlobals();
        }

        public bool RequestProbe(Vector3 worldPosition, Action<RadianceProbeResult> callback)
        {
            if (!EnsureInitialized() || queryPending || queryPositionBuffer == null || queryResultBuffer == null)
                return false;

            singleQueryPosition[0] = worldPosition;
            queryPositionBuffer.SetData(singleQueryPosition);
            BindReadTextures(queryKernel);
            BindLayout(radianceShader);
            radianceShader.SetBuffer(queryKernel, "_RadianceQueryPositions", queryPositionBuffer);
            radianceShader.SetBuffer(queryKernel, "_RadianceQueryResults", queryResultBuffer);
            radianceShader.SetInt("_RadianceQueryCount", 1);
            radianceShader.Dispatch(queryKernel, 1, 1, 1);
            computeDispatchesThisFrame++;

            pendingQueryPosition = worldPosition;
            pendingQueryCallback = callback;
            queryPending = true;
            AsyncGPUReadback.Request(queryResultBuffer, OnProbeReadback);
            return true;
        }

        public bool BuildDebugSamples(
            GraphicsBuffer sampleBuffer,
            int maximumSamples,
            Vector3 center,
            float radius,
            RadianceDebugDirection direction,
            out int sampleCount)
        {
            sampleCount = 0;
            lastDebugProbeCount = 0;
            lastDebugProbeStride = 0;
            if (!initialized || sampleBuffer == null || maximumSamples <= 0)
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
            Vector3Int minimum = ClampProbe(WorldToProbeFloor(Vector3.Max(minimumWS, bounds.min), cellSize));
            Vector3Int maximum = ClampProbe(WorldToProbeFloor(Vector3.Min(maximumWS, bounds.max - cellSize * 0.0001f), cellSize));
            Vector3Int size = maximum - minimum + Vector3Int.one;
            if (size.x <= 0 || size.y <= 0 || size.z <= 0)
                return false;

            long candidateCount = (long)size.x * size.y * size.z;
            int stride = Mathf.Max(1, (int)Math.Ceiling(candidateCount / (double)maximumSamples));
            sampleCount = Mathf.Min(maximumSamples, (int)Math.Ceiling(candidateCount / (double)stride));
            if (sampleCount <= 0)
                return false;

            lastDebugProbeCount = sampleCount;
            lastDebugProbeStride = stride;
            BindReadTextures(debugKernel);
            BindLayout(radianceShader);
            radianceShader.SetBuffer(debugKernel, "_RadianceDebugSamples", sampleBuffer);
            radianceShader.SetInts("_RadianceDebugOffset", minimum.x, minimum.y, minimum.z);
            radianceShader.SetInts("_RadianceDebugSize", size.x, size.y, size.z);
            radianceShader.SetInt("_RadianceDebugSampleCount", sampleCount);
            radianceShader.SetInt("_RadianceDebugSampleStride", stride);
            radianceShader.SetInt("_RadianceDebugDirection", (int)direction);
            radianceShader.SetVector("_RadianceDebugCenter", center);
            radianceShader.SetFloat("_RadianceDebugRadius", Mathf.Max(0f, radius));
            radianceShader.Dispatch(debugKernel, Mathf.CeilToInt(sampleCount / 64f), 1, 1);
            computeDispatchesThisFrame++;
            return true;
        }

        public void GetDirtyTileBounds(List<Bounds> destination)
        {
            destination?.Clear();
            if (destination == null)
                return;
            foreach (Vector3Int coordinate in dirtySet)
                destination.Add(GetTileBounds(coordinate));
        }

        public void GetRecentUpdatedRegions(List<Bounds> destination)
        {
            destination?.Clear();
            if (destination == null)
                return;
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
            if (geometryField == null || radianceShader == null || !SystemInfo.supportsComputeShaders)
                return;

            resolvedOrigin = CalculateFieldOrigin();
            lastFollowOrigin = resolvedOrigin;
            resolution = new Vector3Int(
                Mathf.Max(1, Mathf.CeilToInt(fieldSize.x / probeSpacing)),
                Mathf.Max(1, Mathf.CeilToInt(fieldSize.y / probeSpacing)),
                Mathf.Max(1, Mathf.CeilToInt(fieldSize.z / probeSpacing)));
            int maximum3DSize = SystemInfo.maxTexture3DSize;
            if (resolution.x > maximum3DSize || resolution.y > maximum3DSize || resolution.z > maximum3DSize)
            {
                UnityEngine.Debug.LogError($"Radiance resolution {resolution} exceeds max 3D texture size {maximum3DSize}.", this);
                return;
            }

            tileGridResolution = new Vector3Int(
                Mathf.CeilToInt(resolution.x / (float)tileResolution),
                Mathf.CeilToInt(resolution.y / (float)tileResolution),
                Mathf.CeilToInt(resolution.z / (float)tileResolution));
            textureFormat = ChooseTextureFormat();

            try
            {
                clearKernel = radianceShader.FindKernel("ClearRadiance");
                injectKernel = radianceShader.FindKernel("InjectRadiance");
                queryKernel = radianceShader.FindKernel("QueryRadiance");
                debugKernel = radianceShader.FindKernel("BuildRadianceDebug");
                for (int i = 0; i < radianceTextures.Length; i++)
                    radianceTextures[i] = CreateTexture(i);
                queryPositionBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(float) * 3);
                queryResultBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, RadianceProbeGpuData.Stride);
                emptyEmissiveBuffer = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured,
                    1,
                    EmissiveContributorGpuData.Stride);
            }
            catch (Exception exception)
            {
                UnityEngine.Debug.LogError($"Could not initialize Radiance Field: {exception.Message}", this);
                ReleaseResources();
                return;
            }

            initialized = true;
            ClearTextures();
            CaptureLightingState();
            PublishShaderGlobals();
            if (rebuildOnEnable)
                RebuildAll();
        }

        private GraphicsFormat ChooseTextureFormat()
        {
            if (SystemInfo.IsFormatSupported(GraphicsFormat.R16G16B16A16_SFloat, FormatUsage.LoadStore))
                return GraphicsFormat.R16G16B16A16_SFloat;
            return GraphicsFormat.R32G32B32A32_SFloat;
        }

        private RenderTexture CreateTexture(int directionIndex)
        {
            RenderTextureDescriptor descriptor = new(resolution.x, resolution.y)
            {
                dimension = TextureDimension.Tex3D,
                volumeDepth = resolution.z,
                graphicsFormat = textureFormat,
                depthBufferBits = 0,
                msaaSamples = 1,
                enableRandomWrite = true,
                useMipMap = false,
                autoGenerateMips = false,
                sRGB = false
            };
            RenderTexture texture = new(descriptor)
            {
                name = $"Dynamic GI Radiance {DirectionName(directionIndex)} {resolution.x}x{resolution.y}x{resolution.z}",
                filterMode = FilterMode.Trilinear,
                wrapMode = TextureWrapMode.Clamp,
                hideFlags = HideFlags.HideAndDontSave
            };
            if (!texture.Create())
                throw new InvalidOperationException($"Unity could not create {texture.name} ({textureFormat}).");
            return texture;
        }

        private void ClearTextures()
        {
            BindWriteTextures(clearKernel);
            BindLayout(radianceShader);
            radianceShader.Dispatch(clearKernel,
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
            if (!geometryField.BindSamplingResources(radianceShader, injectKernel))
            {
                EnqueueTile(coordinate);
                return;
            }

            if (skyVisibilityField == null || !skyVisibilityField.BindSamplingResources(radianceShader, injectKernel))
                radianceShader.SetInt("_DynamicGI_SkyVisibilityAvailable", 0);

            BindWriteTextures(injectKernel);
            BindLayout(radianceShader);
            Vector3 toSun = GetSunDirection();
            Vector3 sunRadiance = GetSunRadiance();
            Color linearSky = skyColor.linear;
            radianceShader.SetInts(UpdateOffsetId, offset.x, offset.y, offset.z);
            radianceShader.SetInts(UpdateSizeId, size.x, size.y, size.z);
            radianceShader.SetVector("_RadianceSkyColor", new Vector4(linearSky.r, linearSky.g, linearSky.b, 0f));
            radianceShader.SetFloat("_RadianceSkyIntensity", skyIntensity);
            radianceShader.SetVector("_RadianceSunDirection", toSun);
            radianceShader.SetVector("_RadianceSunColor", sunRadiance);
            radianceShader.SetInt("_RadianceSunEnabled", sunLight != null && sunLight.enabled && sunLight.gameObject.activeInHierarchy ? 1 : 0);
            radianceShader.SetFloat("_RadianceSunTraceDistance", sunTraceDistance);
            radianceShader.SetFloat("_RadianceRayOriginBias", Mathf.Max(rayOriginBias, geometryField.VoxelSize * 0.1f));
            // The Phase-4 local field remains a compatibility path. The shared
            // injection kernel requires an emissive buffer, but only the clipmap
            // consumes registered Phase-7 contributors.
            radianceShader.SetBuffer(injectKernel, "_GIEmissiveContributors", emptyEmissiveBuffer);
            radianceShader.SetInt("_GIEmissiveContributorCount", 0);
            radianceShader.SetInt("_RadianceCascadeIndex", 0);
            int ddaSteps = Mathf.Clamp(Mathf.CeilToInt(sunTraceDistance / geometryField.VoxelSize * 1.8f) + 4, 8, 4096);
            radianceShader.SetInt("_RadianceMaxDdaSteps", ddaSteps);
            radianceShader.Dispatch(injectKernel,
                Mathf.CeilToInt(size.x / 4f),
                Mathf.CeilToInt(size.y / 4f),
                Mathf.CeilToInt(size.z / 4f));

            updatedTilesThisFrame++;
            updatedProbesThisFrame += size.x * size.y * size.z;
            computeDispatchesThisFrame++;
            Bounds updatedBounds = GetTileBounds(coordinate);
            recentRegions.Add(new RecentRegion(updatedBounds, Time.frameCount + recentRegionLifetimeFrames));
            if (recentRegions.Count > 256)
                recentRegions.RemoveAt(0);
        }

        private void BindWriteTextures(int kernel)
        {
            radianceShader.SetTexture(kernel, PositiveXId, radianceTextures[0]);
            radianceShader.SetTexture(kernel, NegativeXId, radianceTextures[1]);
            radianceShader.SetTexture(kernel, PositiveYId, radianceTextures[2]);
            radianceShader.SetTexture(kernel, NegativeYId, radianceTextures[3]);
            radianceShader.SetTexture(kernel, PositiveZId, radianceTextures[4]);
            radianceShader.SetTexture(kernel, NegativeZId, radianceTextures[5]);
        }

        private void BindReadTextures(int kernel)
        {
            radianceShader.SetTexture(kernel, "_RadianceReadPositiveX", radianceTextures[0]);
            radianceShader.SetTexture(kernel, "_RadianceReadNegativeX", radianceTextures[1]);
            radianceShader.SetTexture(kernel, "_RadianceReadPositiveY", radianceTextures[2]);
            radianceShader.SetTexture(kernel, "_RadianceReadNegativeY", radianceTextures[3]);
            radianceShader.SetTexture(kernel, "_RadianceReadPositiveZ", radianceTextures[4]);
            radianceShader.SetTexture(kernel, "_RadianceReadNegativeZ", radianceTextures[5]);
        }

        private void BindLayout(ComputeShader shader)
        {
            shader.SetVector(OriginId, resolvedOrigin);
            shader.SetVector(SizeId, fieldSize);
            shader.SetInts(ResolutionId, resolution.x, resolution.y, resolution.z);
            shader.SetInt(AvailableId, initialized ? 1 : 0);
            shader.SetInts("_RadianceRingOffset", 0, 0, 0);
            shader.SetInt("_RadianceToroidal", 0);
        }

        private void PublishShaderGlobals()
        {
            if (!initialized)
                return;
            Shader.SetGlobalTexture(PositiveXId, radianceTextures[0]);
            Shader.SetGlobalTexture(NegativeXId, radianceTextures[1]);
            Shader.SetGlobalTexture(PositiveYId, radianceTextures[2]);
            Shader.SetGlobalTexture(NegativeYId, radianceTextures[3]);
            Shader.SetGlobalTexture(PositiveZId, radianceTextures[4]);
            Shader.SetGlobalTexture(NegativeZId, radianceTextures[5]);
            Shader.SetGlobalVector(OriginId, resolvedOrigin);
            Shader.SetGlobalVector(SizeId, fieldSize);
            Shader.SetGlobalVector(ResolutionId, new Vector4(resolution.x, resolution.y, resolution.z, 0f));
            Shader.SetGlobalInt(AvailableId, 1);
        }

        private void DetectLightingChanges()
        {
            Vector3 direction = GetSunDirection();
            Vector3 sun = GetSunRadiance();
            Color linearSky = skyColor.linear;
            Vector3 sky = new(linearSky.r * skyIntensity, linearSky.g * skyIntensity, linearSky.b * skyIntensity);
            if ((direction - lastSunDirection).sqrMagnitude > 0.000001f ||
                (sun - lastSunRadiance).sqrMagnitude > 0.000001f ||
                (sky - lastSkyRadiance).sqrMagnitude > 0.000001f)
            {
                lastSunDirection = direction;
                lastSunRadiance = sun;
                lastSkyRadiance = sky;
                InvalidateAll(false);
            }
        }

        private void CaptureLightingState()
        {
            lastSunDirection = GetSunDirection();
            lastSunRadiance = GetSunRadiance();
            Color linearSky = skyColor.linear;
            lastSkyRadiance = new Vector3(linearSky.r, linearSky.g, linearSky.b) * skyIntensity;
        }

        private Vector3 GetSunDirection() => sunLight != null ? -sunLight.transform.forward.normalized : Vector3.up;

        private Vector3 GetSunRadiance()
        {
            if (sunLight == null || !sunLight.enabled || !sunLight.gameObject.activeInHierarchy)
                return Vector3.zero;
            Color linear = sunLight.color.linear;
            float intensity = sunLight.intensity * sunIntensityScale;
            return new Vector3(linear.r * intensity, linear.g * intensity, linear.b * intensity);
        }

        private void InvalidateAll(bool clearExisting)
        {
            if (clearExisting)
            {
                dirtyQueue.Clear();
                dirtySet.Clear();
            }
            for (int z = 0; z < tileGridResolution.z; z++)
            for (int y = 0; y < tileGridResolution.y; y++)
            for (int x = 0; x < tileGridResolution.x; x++)
                EnqueueTile(new Vector3Int(x, y, z));
        }

        private void EnqueueBounds(Bounds worldBounds)
        {
            Bounds bounds = FieldBounds;
            Vector3 minimum = Vector3.Max(worldBounds.min, bounds.min);
            Vector3 maximum = Vector3.Min(worldBounds.max, bounds.max);
            if (minimum.x >= maximum.x || minimum.y >= maximum.y || minimum.z >= maximum.z)
                return;

            Vector3 cellSize = GetCellSize();
            Vector3Int minProbe = ClampProbe(WorldToProbeFloor(minimum, cellSize));
            Vector3Int maxProbe = ClampProbe(WorldToProbeFloor(maximum - cellSize * 0.0001f, cellSize));
            Vector3Int minTile = new(minProbe.x / tileResolution, minProbe.y / tileResolution, minProbe.z / tileResolution);
            Vector3Int maxTile = new(maxProbe.x / tileResolution, maxProbe.y / tileResolution, maxProbe.z / tileResolution);
            for (int z = minTile.z; z <= maxTile.z; z++)
            for (int y = minTile.y; y <= maxTile.y; y++)
            for (int x = minTile.x; x <= maxTile.x; x++)
                EnqueueTile(new Vector3Int(x, y, z));
        }

        private void EnqueueTile(Vector3Int coordinate)
        {
            if (dirtySet.Add(coordinate))
                dirtyQueue.Enqueue(coordinate);
        }

        private Bounds CalculateSunInfluence(Bounds changedBounds)
        {
            Vector3 shiftedCenter = changedBounds.center - GetSunDirection() * sunTraceDistance;
            Bounds influence = changedBounds;
            influence.Encapsulate(new Bounds(shiftedCenter, changedBounds.size));
            influence.Expand(Vector3.one * probeSpacing * 2f);
            return influence;
        }

        private void RefreshSubscriptions()
        {
            if (subscribedGeometryField != geometryField)
            {
                if (subscribedGeometryField != null)
                {
                    subscribedGeometryField.GeometryFieldReset -= OnGeometryReset;
                    subscribedGeometryField.GeometryRegionRebuilt -= OnGeometryRegionRebuilt;
                }
                subscribedGeometryField = geometryField;
                if (subscribedGeometryField != null)
                {
                    subscribedGeometryField.GeometryFieldReset += OnGeometryReset;
                    subscribedGeometryField.GeometryRegionRebuilt += OnGeometryRegionRebuilt;
                }
                configurationDirty = true;
            }

            if (subscribedSkyField != skyVisibilityField)
            {
                if (subscribedSkyField != null)
                {
                    subscribedSkyField.VisibilityFieldReset -= OnSkyReset;
                    subscribedSkyField.VisibilityRegionUpdated -= OnSkyRegionUpdated;
                }
                subscribedSkyField = skyVisibilityField;
                if (subscribedSkyField != null)
                {
                    subscribedSkyField.VisibilityFieldReset += OnSkyReset;
                    subscribedSkyField.VisibilityRegionUpdated += OnSkyRegionUpdated;
                }
                configurationDirty = true;
            }
        }

        private void UnsubscribeSources()
        {
            if (subscribedGeometryField != null)
            {
                subscribedGeometryField.GeometryFieldReset -= OnGeometryReset;
                subscribedGeometryField.GeometryRegionRebuilt -= OnGeometryRegionRebuilt;
            }
            if (subscribedSkyField != null)
            {
                subscribedSkyField.VisibilityFieldReset -= OnSkyReset;
                subscribedSkyField.VisibilityRegionUpdated -= OnSkyRegionUpdated;
            }
            subscribedGeometryField = null;
            subscribedSkyField = null;
        }

        private void OnGeometryReset(Bounds _) => InvalidateAll(false);
        private void OnGeometryRegionRebuilt(Bounds bounds) => EnqueueBounds(CalculateSunInfluence(bounds));
        private void OnSkyReset(Bounds _) => InvalidateAll(false);
        private void OnSkyRegionUpdated(Bounds bounds) => EnqueueBounds(bounds);

        private Vector3 CalculateFieldOrigin()
        {
            Transform target = followTargetTransform;
            if (followTarget && target == null && Camera.main != null)
                target = Camera.main.transform;
            if (!followTarget || target == null)
                return transform.position + fieldCenter - fieldSize * 0.5f;

            Vector3 unsnapped = target.position - fieldSize * 0.5f;
            return new Vector3(
                Mathf.Floor(unsnapped.x / probeSpacing) * probeSpacing,
                Mathf.Floor(unsnapped.y / probeSpacing) * probeSpacing,
                Mathf.Floor(unsnapped.z / probeSpacing) * probeSpacing);
        }

        private Vector3 GetCellSize() => new(fieldSize.x / resolution.x, fieldSize.y / resolution.y, fieldSize.z / resolution.z);

        private Vector3Int WorldToProbeFloor(Vector3 position, Vector3 cellSize)
        {
            Vector3 relative = position - resolvedOrigin;
            return new Vector3Int(
                Mathf.FloorToInt(relative.x / cellSize.x),
                Mathf.FloorToInt(relative.y / cellSize.y),
                Mathf.FloorToInt(relative.z / cellSize.z));
        }

        private Vector3Int ClampProbe(Vector3Int value) => new(
            Mathf.Clamp(value.x, 0, resolution.x - 1),
            Mathf.Clamp(value.y, 0, resolution.y - 1),
            Mathf.Clamp(value.z, 0, resolution.z - 1));

        private Bounds GetTileBounds(Vector3Int coordinate)
        {
            Vector3Int offset = coordinate * tileResolution;
            Vector3Int sampleSize = new(
                Mathf.Min(tileResolution, resolution.x - offset.x),
                Mathf.Min(tileResolution, resolution.y - offset.y),
                Mathf.Min(tileResolution, resolution.z - offset.z));
            Vector3 cell = GetCellSize();
            Vector3 minimum = resolvedOrigin + Vector3.Scale((Vector3)offset, cell);
            Vector3 size = Vector3.Scale((Vector3)sampleSize, cell);
            return new Bounds(minimum + size * 0.5f, size);
        }

        private void OnProbeReadback(AsyncGPUReadbackRequest request)
        {
            bool error = request.hasError;
            RadianceProbeGpuData value = default;
            if (!error)
            {
                var data = request.GetData<RadianceProbeGpuData>();
                error = data.Length == 0;
                if (!error)
                    value = data[0];
            }

            Action<RadianceProbeResult> callback = pendingQueryCallback;
            Vector3 position = pendingQueryPosition;
            pendingQueryCallback = null;
            queryPending = false;
            callback?.Invoke(new RadianceProbeResult(position, value, error));
        }

        private long EstimateGpuBytes()
        {
            long probes = (long)resolution.x * resolution.y * resolution.z;
            long bytesPerTexel = textureFormat == GraphicsFormat.R16G16B16A16_SFloat ? 8L : 16L;
            return probes * bytesPerTexel * 6L + RadianceProbeGpuData.Stride + sizeof(float) * 3L +
                   EmissiveContributorGpuData.Stride;
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
            lastDebugProbeCount = 0;
            lastDebugProbeStride = 0;

            queryPositionBuffer?.Dispose();
            queryResultBuffer?.Dispose();
            emptyEmissiveBuffer?.Dispose();
            queryPositionBuffer = null;
            queryResultBuffer = null;
            emptyEmissiveBuffer = null;
            for (int i = 0; i < radianceTextures.Length; i++)
            {
                RenderTexture texture = radianceTextures[i];
                if (texture == null)
                    continue;
                texture.Release();
                if (Application.isPlaying)
                    Destroy(texture);
                else
                    DestroyImmediate(texture);
                radianceTextures[i] = null;
            }
        }

        private static string DirectionName(int index) => index switch
        {
            0 => "+X",
            1 => "-X",
            2 => "+Y",
            3 => "-Y",
            4 => "+Z",
            _ => "-Z"
        };

        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        private void TryAssignDefaultComputeShader()
        {
#if UNITY_EDITOR
            if (radianceShader == null)
                radianceShader = UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>(DefaultComputePath);
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
