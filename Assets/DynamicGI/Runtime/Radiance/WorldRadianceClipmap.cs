using System;
using System.Collections.Generic;
using System.Diagnostics;
using DynamicGI.Contributors;
using DynamicGI.Geometry;
using DynamicGI.Occlusion;
using Unity.Profiling;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

namespace DynamicGI.Radiance
{
    /// <summary>
    /// Camera/player-centred radiance clipmap. Cascades share a resolution pattern but
    /// increase spacing. Tile-snapped origins and toroidal offsets recycle unchanged
    /// probes without texture copies when the target moves.
    /// </summary>
    [ExecuteAlways]
    [DefaultExecutionOrder(-290)]
    [DisallowMultipleComponent]
    public sealed class WorldRadianceClipmap : MonoBehaviour
    {
        public const int MaximumCascadeCount = 4;
        private const string DefaultComputePath = "Assets/DynamicGI/Shaders/RadianceInject.compute";
        private const string DefaultPropagationComputePath = "Assets/DynamicGI/Shaders/RadiancePropagate.compute";
        private const string DefaultTemporalComputePath = "Assets/DynamicGI/Shaders/RadianceTemporal.compute";
        private static readonly ProfilerMarker UpdateMarker = new("DynamicGI.RadianceClipmap.Update");

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
        private static readonly int RingOffsetId = Shader.PropertyToID("_RadianceRingOffset");
        private static readonly int EmissiveContributorsId = Shader.PropertyToID("_GIEmissiveContributors");
        private static readonly int EmissiveContributorCountId = Shader.PropertyToID("_GIEmissiveContributorCount");
        private static readonly int RadianceCascadeIndexId = Shader.PropertyToID("_RadianceCascadeIndex");
        private static readonly int ClipmapAvailableId = Shader.PropertyToID("_DynamicGI_RadianceClipmapAvailable");
        private static readonly int CascadeCountId = Shader.PropertyToID("_DynamicGI_RadianceCascadeCount");
        private static readonly int[] PropagationDirectTextureIds = CreateDirectionalPropertyIds("_PropagationDirect");
        private static readonly int[] PropagationInputTextureIds = CreateDirectionalPropertyIds("_PropagationInput");
        private static readonly int[] DebugDirectTextureIds = CreateDirectionalPropertyIds("_RadianceDebugDirect");
        private static readonly int[] TemporalCandidateTextureIds = CreateDirectionalPropertyIds("_TemporalCandidate");

        private static readonly int[][] CascadeTextureIds =
        {
            CreateCascadeTextureIds(0),
            CreateCascadeTextureIds(1),
            CreateCascadeTextureIds(2),
            CreateCascadeTextureIds(3)
        };
        private static readonly int[] CascadeOriginIds = CreateCascadePropertyIds("Origin");
        private static readonly int[] CascadeSizeIds = CreateCascadePropertyIds("Size");
        private static readonly int[] CascadeResolutionIds = CreateCascadePropertyIds("Resolution");
        private static readonly int[] CascadeRingOffsetIds = CreateCascadePropertyIds("RingOffset");
        private static readonly int[] CascadeAvailableIds = CreateCascadePropertyIds("Available");

        [Header("Sources")]
        [SerializeField] private WorldGeometryField geometryField;
        [SerializeField] private WorldSkyVisibilityField skyVisibilityField;
        [SerializeField] private Light sunLight;
        [SerializeField] private Transform trackingTarget;

        [Header("Cascades (maximum 4)")]
        [SerializeField] private RadianceCascadeSettings[] cascadeSettings =
        {
            new("Cascade 0", 16, 8, 0.5f, 2, 1, 16, 0.2f, 16),
            new("Cascade 1", 16, 8, 1f, 2, 2, 8, 0.3f, 12),
            new("Cascade 2", 16, 8, 2f, 2, 4, 4, 0.4f, 8)
        };
        [SerializeField, Range(0.4f, 0.9f)] private float cascadeBlendStart = 0.7f;

        [Header("Injection")]
        [SerializeField] private Color skyColor = new(0.22f, 0.38f, 0.65f, 1f);
        [SerializeField, Min(0f)] private float skyIntensity = 0.2f;
        [SerializeField, Min(0f)] private float sunIntensityScale = 0.00001f;
        [SerializeField, Min(1f)] private float sunTraceDistance = 64f;
        [SerializeField, Min(0f)] private float rayOriginBias = 0.08f;

        [Header("Sun updates")]
        [SerializeField, Range(0f, 5f)] private float minimumSunAngularChangeDegrees = 0.1f;
        [SerializeField, Min(0f)] private float minimumSunRadianceChange = 0.002f;
        [SerializeField, Min(0f)] private float minimumSkyRadianceChange = 0.002f;
        [SerializeField] private bool fadeSunBelowHorizon = true;
        [SerializeField, Range(0.1f, 15f)] private float sunHorizonFadeDegrees = 3f;

        [Header("Emissive injection")]
        [SerializeField, Range(1, 256)] private int maximumEmissiveContributors = 64;

        [Header("Diffuse propagation")]
        [SerializeField] private bool enableDiffusePropagation = true;
        [SerializeField, Range(1, 12)] private int propagationIterations = 3;
        [SerializeField, Range(0f, 0.95f)] private float propagationStrength = 0.55f;
        [SerializeField, Range(0f, 1f)] private float propagationDirectionalRetention = 0.35f;
        [SerializeField, Range(0f, 1f)] private float propagationDistanceAttenuation = 0.9f;
        [Tooltip("Probe spacing at which propagation strength and distance attenuation are authored. Other cascades normalize attenuation by their physical spacing.")]
        [SerializeField, Min(0.01f)] private float propagationReferenceSpacing = 0.5f;
        [Tooltip("Neutral occupancy-only reflection used until Geometry Field albedo is available.")]
        [SerializeField, Range(0f, 0.95f)] private float propagationSurfaceReflectivity = 0.35f;
        [SerializeField, Min(0.1f)] private float maximumPropagatedRadiance = 8f;
        [SerializeField, Range(0, 3)] private int maximumPropagationCascadeIndex = 1;

        [Header("Temporal accumulation")]
        [SerializeField] private bool enableTemporalAccumulation = true;

        [Header("Compute")]
        [SerializeField] private ComputeShader radianceShader;
        [SerializeField] private ComputeShader propagationShader;
        [SerializeField] private ComputeShader temporalShader;

        private readonly List<RadianceCascade> cascades = new(MaximumCascadeCount);
        private readonly List<RecentRegion> recentRegions = new();
        private readonly List<ProcessedTile> processedTiles = new(1024);
        private readonly Vector3[] singleQueryPosition = new Vector3[1];
        private WorldGeometryField subscribedGeometryField;
        private WorldSkyVisibilityField subscribedSkyField;
        private GraphicsBuffer queryPositionBuffer;
        private GraphicsBuffer queryResultBuffer;
        private GraphicsBuffer emissiveContributorBuffer;
        private EmissiveContributorGpuData[] emissiveUploadData = Array.Empty<EmissiveContributorGpuData>();
        private GraphicsFormat textureFormat;
        private int clearKernel = -1;
        private int injectKernel = -1;
        private int queryKernel = -1;
        private int debugQueryKernel = -1;
        private int debugKernel = -1;
        private int propagationKernel = -1;
        private int temporalKernel = -1;
        private bool initialized;
        private bool configurationDirty;
        private bool queryPending;
        private int pendingQueryCascade;
        private Vector3 pendingQueryPosition;
        private Action<RadianceClipmapProbeResult> pendingQueryCallback;
        private Vector3 lastSunDirection;
        private Vector3 lastSunRadiance;
        private Vector3 lastSkyRadiance;
        private int updatedTilesThisFrame;
        private int updatedProbesThisFrame;
        private int exposedProbesThisFrame;
        private int recycledProbesThisFrame;
        private int originMovesThisFrame;
        private int computeDispatchesThisFrame;
        private int sunRevision;
        private int lightingRefreshesThisFrame;
        private int activeEmissiveContributors;
        private int emissiveRevision;
        private int emissiveChangesThisFrame;
        private int pendingEmissiveChanges;
        private float maximumEmissiveRange;
        private bool emissiveUploadDirty = true;
        private bool emissiveOverflowWarningIssued;
        private int propagationDispatchesThisFrame;
        private int propagatedProbesThisFrame;
        private int temporalDispatchesThisFrame;
        private int temporalTilesThisFrame;
        private int temporalProbesThisFrame;
        private int temporalResetTilesThisFrame;
        private double updateCpuMilliseconds;
        private int lastDebugProbeCount;
        private int lastDebugProbeStride;

        public static WorldRadianceClipmap Active { get; private set; }

        public WorldGeometryField GeometryField => geometryField;
        public WorldSkyVisibilityField SkyVisibilityField => skyVisibilityField;
        public Light SunLight => sunLight;
        public Transform TrackingTarget => trackingTarget;
        public int CascadeCount => cascades.Count;
        public bool IsInitialized => initialized;
        public float CascadeBlendStart => cascadeBlendStart;
        public int SunRevision => sunRevision;
        public Vector3 CurrentSunDirection => GetSunDirection();
        public Vector3 CurrentSunRadiance => GetSunRadiance();
        public float CurrentSunHorizonFactor => GetSunHorizonFactor(GetSunDirection());
        public int ActiveEmissiveContributorCount => activeEmissiveContributors;
        public int EmissiveRevision => emissiveRevision;
        public bool DiffusePropagationEnabled =>
            enableDiffusePropagation && propagationStrength > 0f && propagationShader != null;
        public int PropagationIterations => propagationIterations;
        public float PropagationStrength => propagationStrength;
        public int MaximumPropagationCascadeIndex => maximumPropagationCascadeIndex;
        public bool TemporalAccumulationEnabled => enableTemporalAccumulation && temporalShader != null && temporalKernel >= 0;
        public int LastDebugProbeCount => lastDebugProbeCount;
        public int LastDebugProbeStride => lastDebugProbeStride;

        public RadianceClipmapStats Stats
        {
            get
            {
                int probes = 0;
                int dirty = 0;
                int temporal = 0;
                long memory = RadianceProbeGpuData.Stride + sizeof(float) * 3L +
                              (long)maximumEmissiveContributors * EmissiveContributorGpuData.Stride;
                for (int i = 0; i < cascades.Count; i++)
                {
                    probes += cascades[i].ProbeCount;
                    dirty += cascades[i].DirtyTileCount;
                    temporal += cascades[i].TemporalTileCount;
                    memory += cascades[i].EstimateGpuBytes();
                }
                return new RadianceClipmapStats(
                    cascades.Count,
                    probes,
                    dirty,
                    updatedTilesThisFrame,
                    updatedProbesThisFrame,
                    exposedProbesThisFrame,
                    recycledProbesThisFrame,
                    originMovesThisFrame,
                    computeDispatchesThisFrame,
                    sunRevision,
                    lightingRefreshesThisFrame,
                    activeEmissiveContributors,
                    emissiveRevision,
                    emissiveChangesThisFrame,
                    DiffusePropagationEnabled,
                    propagationIterations,
                    propagationDispatchesThisFrame,
                    propagatedProbesThisFrame,
                    TemporalAccumulationEnabled,
                    temporal,
                    temporalDispatchesThisFrame,
                    temporalTilesThisFrame,
                    temporalProbesThisFrame,
                    temporalResetTilesThisFrame,
                    memory,
                    updateCpuMilliseconds);
            }
        }

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
            GIEmissiveContributor.RegistryChanged -= OnEmissiveContributorChanged;
            GIEmissiveContributor.RegistryChanged += OnEmissiveContributorChanged;
            if (Active != null && Active != this)
                UnityEngine.Debug.LogWarning("Multiple WorldRadianceClipmap instances are enabled. Shader globals use the latest one.", this);
            Active = this;
            Initialize();
        }

        private void OnDisable()
        {
            GIEmissiveContributor.RegistryChanged -= OnEmissiveContributorChanged;
            UnsubscribeSources();
            ReleaseResources();
            if (Active == this)
            {
                Active = null;
                Shader.SetGlobalInt(ClipmapAvailableId, 0);
                Shader.SetGlobalInt(CascadeCountId, 0);
            }
        }

        private void OnValidate()
        {
            cascadeBlendStart = Mathf.Clamp(cascadeBlendStart, 0.4f, 0.9f);
            skyIntensity = Mathf.Max(0f, skyIntensity);
            sunIntensityScale = Mathf.Max(0f, sunIntensityScale);
            sunTraceDistance = Mathf.Max(1f, sunTraceDistance);
            rayOriginBias = Mathf.Max(0f, rayOriginBias);
            minimumSunAngularChangeDegrees = Mathf.Clamp(minimumSunAngularChangeDegrees, 0f, 5f);
            minimumSunRadianceChange = Mathf.Max(0f, minimumSunRadianceChange);
            minimumSkyRadianceChange = Mathf.Max(0f, minimumSkyRadianceChange);
            sunHorizonFadeDegrees = Mathf.Clamp(sunHorizonFadeDegrees, 0.1f, 15f);
            maximumEmissiveContributors = Mathf.Clamp(maximumEmissiveContributors, 1, 256);
            propagationIterations = Mathf.Clamp(propagationIterations, 1, 12);
            propagationStrength = Mathf.Clamp(propagationStrength, 0f, 0.95f);
            propagationDirectionalRetention = Mathf.Clamp01(propagationDirectionalRetention);
            propagationDistanceAttenuation = Mathf.Clamp01(propagationDistanceAttenuation);
            propagationReferenceSpacing = Mathf.Max(0.01f, propagationReferenceSpacing);
            propagationSurfaceReflectivity = Mathf.Clamp(propagationSurfaceReflectivity, 0f, 0.95f);
            maximumPropagatedRadiance = Mathf.Max(0.1f, maximumPropagatedRadiance);
            maximumPropagationCascadeIndex = Mathf.Clamp(maximumPropagationCascadeIndex, 0, 3);
            if (cascadeSettings == null)
                cascadeSettings = Array.Empty<RadianceCascadeSettings>();
            for (int i = 0; i < cascadeSettings.Length; i++)
                cascadeSettings[i]?.Sanitize();
            TryAssignDefaultComputeShader();
            if (isActiveAndEnabled)
                configurationDirty = true;
        }

        private void Update()
        {
            RefreshSubscriptions();
            if (configurationDirty)
                Initialize();

            ResetFrameStats();
            if (!initialized || geometryField == null || !geometryField.IsInitialized)
            {
                Shader.SetGlobalInt(ClipmapAvailableId, 0);
                return;
            }

            UploadEmissiveContributorsIfNeeded();

            Vector3 targetPosition = ResolveTrackingPosition();
            for (int i = 0; i < cascades.Count; i++)
            {
                RadianceCascade cascade = cascades[i];
                if (!cascade.UpdateOrigin(targetPosition))
                    continue;
                originMovesThisFrame++;
                exposedProbesThisFrame += cascade.LastExposedProbes;
                recycledProbesThisFrame += cascade.LastRecycledProbes;
                if (cascade.LastExposedProbes == cascade.ProbeCount && cascade.LastRecycledProbes == 0)
                {
                    // A teleport reuses no logical probes. Clear the old physical ring so
                    // unprocessed tiles cannot briefly display radiance from another place.
                    ClearCascade(cascade);
                }
            }

            DetectLightingChanges();
            PublishShaderGlobals();
            bool skyReady = skyVisibilityField == null ||
                            (skyVisibilityField.IsInitialized && skyVisibilityField.DirtyTileCount == 0);
            if (geometryField.DirtyBrickCount == 0 && skyReady)
            {
                long startTimestamp = Stopwatch.GetTimestamp();
                using (UpdateMarker.Auto())
                {
                    int frame = Time.frameCount;
                    for (int i = 0; i < cascades.Count; i++)
                    {
                        RadianceCascade cascade = cascades[i];
                        if (frame % cascade.UpdateIntervalFrames == 0)
                            ProcessCascade(cascade, cascade.UpdateBudgetTiles);
                    }
                }
                updateCpuMilliseconds = (Stopwatch.GetTimestamp() - startTimestamp) * 1000.0 / Stopwatch.Frequency;
            }

            int currentFrame = Time.frameCount;
            for (int i = recentRegions.Count - 1; i >= 0; i--)
                if (recentRegions[i].ExpiryFrame < currentFrame) recentRegions.RemoveAt(i);
        }

        [ContextMenu("Rebuild All Radiance Cascades")]
        public void RebuildAll()
        {
            if (!EnsureInitialized())
                return;
            recentRegions.Clear();
            InvalidateAllCascades(true);
        }

        /// <summary>
        /// Explicit hook for custom day/night controllers. Ordinary transform, color,
        /// intensity, enabled-state, and sky changes are detected automatically.
        /// </summary>
        [ContextMenu("Force Sun / Sky Refresh")]
        public void ForceLightingRefresh()
        {
            if (!EnsureInitialized())
                return;
            CaptureLightingState();
            sunRevision++;
            lightingRefreshesThisFrame++;
            InvalidateAllCascades();
        }

        [ContextMenu("Process All Dirty Cascade Tiles")]
        public void ProcessAllDirtyNow()
        {
            if (!EnsureInitialized())
                return;
            UploadEmissiveContributorsIfNeeded();
            if (geometryField == null || !geometryField.IsInitialized || geometryField.DirtyBrickCount > 0 ||
                (skyVisibilityField != null &&
                 (!skyVisibilityField.IsInitialized || skyVisibilityField.DirtyTileCount > 0)))
            {
                UnityEngine.Debug.LogWarning("Radiance Clipmap is waiting for Geometry/Sky Visibility updates.", this);
                return;
            }
            for (int i = 0; i < cascades.Count; i++)
            {
                RadianceCascade cascade = cascades[i];
                while (cascade.PendingWorkTileCount > 0)
                    ProcessCascade(cascade, cascade.PendingWorkTileCount);
            }
            PublishShaderGlobals();
        }

        /// <summary>
        /// Generates every pending direct/propagated candidate and applies exactly one
        /// temporal resolve. Remaining smoothing work stays queued. This is primarily a
        /// deterministic debug and validation hook; ordinary runtime work uses Update.
        /// </summary>
        public void ProcessAllCandidateUpdatesNow()
        {
            if (!CanProcessRadianceNow())
                return;
            UploadEmissiveContributorsIfNeeded();
            for (int i = 0; i < cascades.Count; i++)
            {
                RadianceCascade cascade = cascades[i];
                while (cascade.DirtyTileCount > 0)
                    ProcessCandidateTiles(cascade, cascade.DirtyTileCount);
            }
            PublishShaderGlobals();
        }

        /// <summary>Completes queued temporal lerps without rebuilding source candidates.</summary>
        public void ProcessAllTemporalNow()
        {
            if (!CanProcessRadianceNow())
                return;
            for (int i = 0; i < cascades.Count; i++)
            {
                RadianceCascade cascade = cascades[i];
                while (cascade.TemporalTileCount > 0)
                    ProcessTemporalTiles(cascade, cascade.TemporalTileCount);
            }
            PublishShaderGlobals();
        }

        public void InvalidateRegion(Bounds worldBounds)
        {
            InvalidateRegion(worldBounds, false);
        }

        private void InvalidateRegion(Bounds worldBounds, bool resetTemporal)
        {
            if (!EnsureInitialized())
                return;
            for (int i = 0; i < cascades.Count; i++)
                cascades[i].InvalidateWorldBounds(
                    ExpandForPropagation(worldBounds, cascades[i]),
                    resetTemporal);
        }

        /// <summary>
        /// Runtime quality hook used by scalability controllers and validation. A
        /// change invalidates the clipmap because resolved textures contain the old
        /// propagation equilibrium.
        /// </summary>
        public void SetPropagationStrength(float value)
        {
            value = Mathf.Clamp(value, 0f, 0.95f);
            if (Mathf.Approximately(propagationStrength, value))
                return;
            propagationStrength = value;
            if (initialized)
                InvalidateAllCascades();
        }

        /// <summary>
        /// Runtime scalability/debug hook. Changing alpha or convergence length keeps
        /// existing radiance and affects the next invalidation for this cascade.
        /// </summary>
        public bool SetTemporalParameters(int cascadeIndex, float alpha, int convergenceSteps)
        {
            if (!EnsureInitialized() || !TryGetCascade(cascadeIndex, out RadianceCascade cascade))
                return false;
            cascade.SetTemporalParameters(alpha, convergenceSteps);
            return true;
        }

        public bool TryGetCascade(int index, out RadianceCascade cascade)
        {
            if (index >= 0 && index < cascades.Count)
            {
                cascade = cascades[index];
                return true;
            }
            cascade = null;
            return false;
        }

        public void GetCascadeStats(List<RadianceCascadeRuntimeStats> destination)
        {
            if (destination == null)
                return;
            destination.Clear();
            for (int i = 0; i < cascades.Count; i++)
                destination.Add(new RadianceCascadeRuntimeStats(cascades[i]));
        }

        public void GetDirtyTileBounds(int cascadeIndex, List<Bounds> destination)
        {
            if (TryGetCascade(cascadeIndex, out RadianceCascade cascade))
                cascade.GetDirtyTileBounds(destination);
            else
                destination?.Clear();
        }

        public void GetRecentUpdatedRegions(int cascadeIndex, List<Bounds> destination)
        {
            if (destination == null)
                return;
            destination.Clear();
            for (int i = 0; i < recentRegions.Count; i++)
                if (recentRegions[i].CascadeIndex == cascadeIndex) destination.Add(recentRegions[i].Bounds);
        }

        /// <summary>
        /// Explicitly binds the clipmap to a compute kernel. Unity material shaders can
        /// consume the published globals directly, but compute shaders do not reliably
        /// inherit global Texture3D resources on every graphics backend.
        /// </summary>
        public bool BindSamplingResources(ComputeShader shader, int kernel)
        {
            if (!EnsureInitialized() || shader == null)
                return false;

            shader.SetInt("_DynamicGI_RadianceClipmapAvailable", 1);
            shader.SetInt("_DynamicGI_RadianceCascadeCount", cascades.Count);
            shader.SetFloat("_DynamicGI_RadianceCascadeBlendStart", cascadeBlendStart);
            RadianceCascade fallback = cascades[0];

            // RadianceField.hlsl also declares the Phase-4 fallback. A compute kernel
            // requires every referenced texture slot to be bound even when the runtime
            // branch selects the clipmap path.
            shader.SetInt("_DynamicGI_RadianceAvailable", 0);
            shader.SetTexture(kernel, "_DynamicGI_RadiancePositiveX", fallback.Textures[0]);
            shader.SetTexture(kernel, "_DynamicGI_RadianceNegativeX", fallback.Textures[1]);
            shader.SetTexture(kernel, "_DynamicGI_RadiancePositiveY", fallback.Textures[2]);
            shader.SetTexture(kernel, "_DynamicGI_RadianceNegativeY", fallback.Textures[3]);
            shader.SetTexture(kernel, "_DynamicGI_RadiancePositiveZ", fallback.Textures[4]);
            shader.SetTexture(kernel, "_DynamicGI_RadianceNegativeZ", fallback.Textures[5]);
            for (int i = 0; i < MaximumCascadeCount; i++)
            {
                bool available = i < cascades.Count;
                shader.SetInt(CascadeAvailableIds[i], available ? 1 : 0);
                RadianceCascade cascade = available ? cascades[i] : fallback;
                for (int direction = 0; direction < 6; direction++)
                    shader.SetTexture(kernel, CascadeTextureIds[i][direction], cascade.Textures[direction]);
                shader.SetVector(CascadeOriginIds[i], cascade.OriginWS);
                shader.SetVector(CascadeSizeIds[i], cascade.SizeWS);
                shader.SetVector(CascadeResolutionIds[i], (Vector3)cascade.Resolution);
                shader.SetVector(CascadeRingOffsetIds[i], (Vector3)cascade.RingOffset);
            }
            return true;
        }

        public bool RequestProbe(Vector3 worldPosition, Action<RadianceClipmapProbeResult> callback)
        {
            return RequestProbeInternal(worldPosition, RadianceDebugSource.Resolved, false, callback);
        }

        /// <summary>
        /// Queries the same source shown by the clipmap debug renderer. The propagation
        /// delta is evaluated on the GPU from the persistent resolved/direct textures.
        /// </summary>
        public bool RequestDebugProbe(
            Vector3 worldPosition,
            RadianceDebugSource source,
            Action<RadianceClipmapProbeResult> callback)
        {
            return RequestProbeInternal(worldPosition, source, true, callback);
        }

        private bool RequestProbeInternal(
            Vector3 worldPosition,
            RadianceDebugSource source,
            bool debugQuery,
            Action<RadianceClipmapProbeResult> callback)
        {
            if (!EnsureInitialized() || queryPending || queryPositionBuffer == null || queryResultBuffer == null)
                return false;
            int cascadeIndex = FindFinestContainingCascade(worldPosition);
            if (cascadeIndex < 0)
                return false;
            RadianceCascade cascade = cascades[cascadeIndex];
            singleQueryPosition[0] = worldPosition;
            queryPositionBuffer.SetData(singleQueryPosition);
            int kernel = debugQuery ? debugQueryKernel : queryKernel;
            if (kernel < 0)
                return false;
            BindReadTextures(cascade, kernel);
            if (debugQuery)
            {
                BindDebugDirectTextures(cascade, kernel);
                radianceShader.SetInt("_RadianceDebugSource", (int)source);
            }
            BindCascadeLayout(cascade);
            radianceShader.SetBuffer(kernel, "_RadianceQueryPositions", queryPositionBuffer);
            radianceShader.SetBuffer(kernel, "_RadianceQueryResults", queryResultBuffer);
            radianceShader.SetInt("_RadianceQueryCount", 1);
            radianceShader.Dispatch(kernel, 1, 1, 1);
            computeDispatchesThisFrame++;

            pendingQueryCascade = cascadeIndex;
            pendingQueryPosition = worldPosition;
            pendingQueryCallback = callback;
            queryPending = true;
            AsyncGPUReadback.Request(queryResultBuffer, OnProbeReadback);
            return true;
        }

        public bool BuildDebugSamples(
            int cascadeIndex,
            GraphicsBuffer sampleBuffer,
            int maximumSamples,
            Vector3 center,
            float radius,
            RadianceDebugDirection direction,
            out int sampleCount)
        {
            return BuildDebugSamples(
                cascadeIndex,
                sampleBuffer,
                maximumSamples,
                center,
                radius,
                direction,
                RadianceDebugSource.Resolved,
                out sampleCount);
        }

        public bool BuildDebugSamples(
            int cascadeIndex,
            GraphicsBuffer sampleBuffer,
            int maximumSamples,
            Vector3 center,
            float radius,
            RadianceDebugDirection direction,
            RadianceDebugSource source,
            out int sampleCount)
        {
            sampleCount = 0;
            lastDebugProbeCount = 0;
            lastDebugProbeStride = 0;
            if (!initialized || sampleBuffer == null || maximumSamples <= 0 ||
                !TryGetCascade(cascadeIndex, out RadianceCascade cascade))
            {
                return false;
            }

            Bounds bounds = cascade.WorldBounds;
            if (radius > 0f && !bounds.Intersects(new Bounds(center, Vector3.one * radius * 2f)))
                return false;
            Vector3 cell = Vector3.one * cascade.ProbeSpacing;
            Vector3 radiusVector = Vector3.one * Mathf.Max(0f, radius);
            Vector3 minWorld = radius > 0f ? center - radiusVector : bounds.min;
            Vector3 maxWorld = radius > 0f ? center + radiusVector : bounds.max;
            Vector3Int minimum = ClampProbe(WorldToProbeFloor(Vector3.Max(minWorld, bounds.min), cascade), cascade.Resolution);
            Vector3Int maximum = ClampProbe(WorldToProbeFloor(Vector3.Min(maxWorld, bounds.max - cell * 0.0001f), cascade), cascade.Resolution);
            Vector3Int size = maximum - minimum + Vector3Int.one;
            if (size.x <= 0 || size.y <= 0 || size.z <= 0)
                return false;

            long candidates = (long)size.x * size.y * size.z;
            int stride = Mathf.Max(1, (int)Math.Ceiling(candidates / (double)maximumSamples));
            sampleCount = Mathf.Min(maximumSamples, (int)Math.Ceiling(candidates / (double)stride));
            if (sampleCount <= 0)
                return false;

            lastDebugProbeCount = sampleCount;
            lastDebugProbeStride = stride;
            BindReadTextures(cascade, debugKernel);
            BindDebugDirectTextures(cascade, debugKernel);
            BindCascadeLayout(cascade);
            radianceShader.SetBuffer(debugKernel, "_RadianceDebugSamples", sampleBuffer);
            radianceShader.SetInts("_RadianceDebugOffset", minimum.x, minimum.y, minimum.z);
            radianceShader.SetInts("_RadianceDebugSize", size.x, size.y, size.z);
            radianceShader.SetInt("_RadianceDebugSampleCount", sampleCount);
            radianceShader.SetInt("_RadianceDebugSampleStride", stride);
            radianceShader.SetInt("_RadianceDebugDirection", (int)direction);
            radianceShader.SetInt("_RadianceDebugSource", (int)source);
            radianceShader.SetVector("_RadianceDebugCenter", center);
            radianceShader.SetFloat("_RadianceDebugRadius", Mathf.Max(0f, radius));
            radianceShader.Dispatch(debugKernel, Mathf.CeilToInt(sampleCount / 64f), 1, 1);
            computeDispatchesThisFrame++;
            return true;
        }

        private bool EnsureInitialized()
        {
            if (!initialized)
                Initialize();
            return initialized;
        }

        private bool CanProcessRadianceNow()
        {
            if (!EnsureInitialized())
                return false;
            if (geometryField != null && geometryField.IsInitialized && geometryField.DirtyBrickCount == 0 &&
                (skyVisibilityField == null ||
                 (skyVisibilityField.IsInitialized && skyVisibilityField.DirtyTileCount == 0)))
            {
                return true;
            }

            UnityEngine.Debug.LogWarning("Radiance Clipmap is waiting for Geometry/Sky Visibility updates.", this);
            return false;
        }

        private void Initialize()
        {
            configurationDirty = false;
            ReleaseResources();
            TryAssignDefaultComputeShader();
            if (geometryField == null || radianceShader == null || !SystemInfo.supportsComputeShaders)
                return;
            try
            {
                clearKernel = radianceShader.FindKernel("ClearRadiance");
                injectKernel = radianceShader.FindKernel("InjectRadiance");
                queryKernel = radianceShader.FindKernel("QueryRadiance");
                debugQueryKernel = radianceShader.FindKernel("QueryRadianceClipmapDebug");
                debugKernel = radianceShader.FindKernel("BuildRadianceClipmapDebug");
                propagationKernel = enableDiffusePropagation && propagationShader != null
                    ? propagationShader.FindKernel("PropagateRadiance")
                    : -1;
                temporalKernel = enableTemporalAccumulation && temporalShader != null
                    ? temporalShader.FindKernel("AccumulateRadianceTemporal")
                    : -1;
                textureFormat = ChooseTextureFormat();
                Vector3 target = ResolveTrackingPosition();
                int settingsCount = Mathf.Min(MaximumCascadeCount, cascadeSettings?.Length ?? 0);
                for (int i = 0; i < settingsCount; i++)
                {
                    RadianceCascadeSettings settings = cascadeSettings[i];
                    if (settings == null || !settings.Enabled)
                        continue;
                    int cascadeIndex = cascades.Count;
                    bool allocatePropagation = propagationKernel >= 0 &&
                                                cascadeIndex <= maximumPropagationCascadeIndex;
                    RadianceCascade cascade = new(
                        cascadeIndex,
                        settings,
                        textureFormat,
                        target,
                        allocatePropagation);
                    cascades.Add(cascade);
                    ClearCascade(cascade);
                }
                if (cascades.Count == 0)
                    throw new InvalidOperationException("At least one enabled Radiance Cascade is required.");
                queryPositionBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, sizeof(float) * 3);
                queryResultBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, RadianceProbeGpuData.Stride);
                emissiveContributorBuffer = new GraphicsBuffer(
                    GraphicsBuffer.Target.Structured,
                    maximumEmissiveContributors,
                    EmissiveContributorGpuData.Stride);
                emissiveUploadData = new EmissiveContributorGpuData[maximumEmissiveContributors];
                emissiveUploadDirty = true;
            }
            catch (Exception exception)
            {
                UnityEngine.Debug.LogError($"Could not initialize Radiance Clipmap: {exception.Message}", this);
                ReleaseResources();
                return;
            }

            initialized = true;
            UploadEmissiveContributorsIfNeeded();
            CaptureLightingState();
            PublishShaderGlobals();
        }

        private void ProcessCascade(RadianceCascade cascade, int budget)
        {
            budget = Mathf.Max(0, budget);
            if (budget == 0)
                return;

            int candidateBudget = budget;
            if (TemporalAccumulationEnabled && cascade.DirtyTileCount > 0 && cascade.TemporalTileCount > 0)
            {
                if (budget == 1)
                {
                    bool candidateTurn =
                        ((Time.frameCount / cascade.UpdateIntervalFrames + cascade.Index) & 1) == 0;
                    candidateBudget = candidateTurn ? 1 : 0;
                }
                else
                {
                    candidateBudget = (budget + 1) / 2;
                }
            }
            int candidates = ProcessCandidateTiles(cascade, candidateBudget);
            int remainingBudget = budget - candidates;
            if (TemporalAccumulationEnabled && remainingBudget > 0)
                ProcessTemporalTiles(cascade, remainingBudget);
        }

        private int ProcessCandidateTiles(RadianceCascade cascade, int budget)
        {
            processedTiles.Clear();
            int processed = 0;
            while (processed < budget && cascade.TryDequeueDirty(
                       out Vector3Int globalTile,
                       out Vector3Int localTile,
                       out bool resetTemporal))
            {
                DispatchInjectionTile(cascade, localTile);
                processedTiles.Add(new ProcessedTile(globalTile, localTile, resetTemporal));
                processed++;
            }

            bool propagate = ShouldPropagate(cascade);
            for (int i = 0; i < processedTiles.Count; i++)
            {
                ProcessedTile tile = processedTiles[i];
                if (propagate)
                    DispatchPropagationTile(cascade, tile.LocalTile);
                if (TemporalAccumulationEnabled)
                {
                    DispatchTemporalTile(cascade, tile.LocalTile, tile.ResetTemporal, tile.ResetTemporal);
                    if (tile.ResetTemporal || cascade.TemporalAlpha >= 0.999f ||
                        cascade.TemporalConvergenceSteps <= 1)
                    {
                        cascade.CancelTemporalConvergence(tile.GlobalTile);
                    }
                    else
                    {
                        cascade.ScheduleTemporalConvergence(
                            tile.GlobalTile,
                            cascade.TemporalConvergenceSteps - 1);
                    }
                }
                Bounds updatedBounds = cascade.GetGlobalTileBounds(tile.GlobalTile);
                if (propagate)
                    updatedBounds = ExpandForPropagation(updatedBounds, cascade);
                recentRegions.Add(new RecentRegion(cascade.Index, updatedBounds, Time.frameCount + 180));
                if (recentRegions.Count > 512) recentRegions.RemoveAt(0);
            }
            return processed;
        }

        private int ProcessTemporalTiles(RadianceCascade cascade, int budget)
        {
            int processed = 0;
            while (processed < budget && cascade.TryDequeueTemporal(
                       out Vector3Int globalTile,
                       out Vector3Int localTile,
                       out int remainingSteps))
            {
                bool finalStep = remainingSteps <= 1;
                DispatchTemporalTile(cascade, localTile, finalStep, false);
                if (remainingSteps > 1)
                    cascade.ScheduleTemporalConvergence(globalTile, remainingSteps - 1);
                processed++;
            }
            return processed;
        }

        private void DispatchInjectionTile(RadianceCascade cascade, Vector3Int localTile)
        {
            if (!geometryField.BindSamplingResources(radianceShader, injectKernel))
                return;
            if (skyVisibilityField == null || !skyVisibilityField.BindSamplingResources(radianceShader, injectKernel))
                radianceShader.SetInt("_DynamicGI_SkyVisibilityAvailable", 0);

            IReadOnlyList<RenderTexture> injectionTarget =
                ShouldPropagate(cascade) || TemporalAccumulationEnabled
                    ? cascade.DirectTextures
                    : cascade.Textures;
            BindWriteTextures(radianceShader, injectKernel, injectionTarget);
            BindCascadeLayout(cascade);
            Vector3 toSun = GetSunDirection();
            Vector3 sun = GetSunRadiance();
            Color sky = skyColor.linear;
            Vector3Int offset = localTile * cascade.TileResolution;
            radianceShader.SetInts("_RadianceUpdateOffset", offset.x, offset.y, offset.z);
            radianceShader.SetInts("_RadianceUpdateSize", cascade.TileResolution, cascade.TileResolution, cascade.TileResolution);
            radianceShader.SetVector("_RadianceSkyColor", new Vector4(sky.r, sky.g, sky.b, 0f));
            radianceShader.SetFloat("_RadianceSkyIntensity", skyIntensity);
            radianceShader.SetVector("_RadianceSunDirection", toSun);
            radianceShader.SetVector("_RadianceSunColor", sun);
            radianceShader.SetInt("_RadianceSunEnabled", sunLight != null && sunLight.enabled && sunLight.gameObject.activeInHierarchy ? 1 : 0);
            radianceShader.SetFloat("_RadianceSunTraceDistance", sunTraceDistance);
            radianceShader.SetFloat("_RadianceRayOriginBias", Mathf.Max(rayOriginBias, geometryField.VoxelSize * 0.1f));
            radianceShader.SetBuffer(injectKernel, EmissiveContributorsId, emissiveContributorBuffer);
            radianceShader.SetInt(EmissiveContributorCountId, activeEmissiveContributors);
            radianceShader.SetInt(RadianceCascadeIndexId, cascade.Index);
            float maximumTraceDistance = Mathf.Max(sunTraceDistance, maximumEmissiveRange);
            int ddaSteps = Mathf.Clamp(Mathf.CeilToInt(maximumTraceDistance / geometryField.VoxelSize * 1.8f) + 4, 8, 4096);
            radianceShader.SetInt("_RadianceMaxDdaSteps", ddaSteps);
            radianceShader.Dispatch(injectKernel,
                Mathf.CeilToInt(cascade.TileResolution / 4f),
                Mathf.CeilToInt(cascade.TileResolution / 4f),
                Mathf.CeilToInt(cascade.TileResolution / 4f));
            updatedTilesThisFrame++;
            updatedProbesThisFrame += cascade.TileResolution * cascade.TileResolution * cascade.TileResolution;
            computeDispatchesThisFrame++;
        }

        private void DispatchPropagationTile(RadianceCascade cascade, Vector3Int localTile)
        {
            if (!geometryField.BindSamplingResources(propagationShader, propagationKernel))
                return;

            BindCascadeLayout(propagationShader, cascade);
            BindPropagationDirectTextures(cascade.DirectTextures);
            propagationShader.SetFloat("_PropagationStrength", propagationStrength);
            propagationShader.SetFloat("_PropagationDirectionalRetention", propagationDirectionalRetention);
            propagationShader.SetFloat("_PropagationDistanceAttenuation", propagationDistanceAttenuation);
            propagationShader.SetFloat("_PropagationReferenceSpacing", propagationReferenceSpacing);
            propagationShader.SetFloat("_PropagationSurfaceReflectivity", propagationSurfaceReflectivity);
            propagationShader.SetFloat("_PropagationMaximumRadiance", maximumPropagatedRadiance);
            float bias = Mathf.Max(rayOriginBias, geometryField.VoxelSize * 0.1f);
            propagationShader.SetFloat("_RadianceRayOriginBias", bias);
            int ddaSteps = Mathf.Clamp(
                Mathf.CeilToInt(cascade.ProbeSpacing / geometryField.VoxelSize * 1.8f) + 4,
                4,
                64);
            propagationShader.SetInt("_RadianceMaxDdaSteps", ddaSteps);

            IReadOnlyList<RenderTexture> previousOutput = cascade.DirectTextures;
            for (int iteration = 0; iteration < propagationIterations; iteration++)
            {
                bool finalIteration = iteration == propagationIterations - 1;
                IReadOnlyList<RenderTexture> output = finalIteration
                    ? TemporalAccumulationEnabled
                        ? cascade.CandidateTextures
                        : cascade.Textures
                    : (iteration & 1) == 0
                        ? cascade.PropagationScratchTexturesA
                        : cascade.PropagationScratchTexturesB;
                BindPropagationInputTextures(previousOutput);
                BindWriteTextures(propagationShader, propagationKernel, output);

                int halo = propagationIterations - iteration - 1;
                CalculatePropagationRegion(cascade, localTile, halo, out Vector3Int offset, out Vector3Int size);
                propagationShader.SetInts("_PropagationUpdateOffset", offset.x, offset.y, offset.z);
                propagationShader.SetInts("_PropagationUpdateSize", size.x, size.y, size.z);
                propagationShader.Dispatch(
                    propagationKernel,
                    Mathf.CeilToInt(size.x / 4f),
                    Mathf.CeilToInt(size.y / 4f),
                    Mathf.CeilToInt(size.z / 4f));
                previousOutput = output;
                propagationDispatchesThisFrame++;
                propagatedProbesThisFrame += size.x * size.y * size.z;
                computeDispatchesThisFrame++;
            }
        }

        private void DispatchTemporalTile(
            RadianceCascade cascade,
            Vector3Int localTile,
            bool forceReplace,
            bool countAsHistoryReset)
        {
            if (!TemporalAccumulationEnabled)
                return;

            IReadOnlyList<RenderTexture> candidate = ShouldPropagate(cascade)
                ? cascade.CandidateTextures
                : cascade.DirectTextures;
            BindCascadeLayout(temporalShader, cascade);
            BindWriteTextures(temporalShader, temporalKernel, cascade.Textures);
            for (int i = 0; i < 6; i++)
                temporalShader.SetTexture(temporalKernel, TemporalCandidateTextureIds[i], candidate[i]);

            Vector3Int offset = localTile * cascade.TileResolution;
            int tileResolution = cascade.TileResolution;
            temporalShader.SetInts("_TemporalUpdateOffset", offset.x, offset.y, offset.z);
            temporalShader.SetInts("_TemporalUpdateSize", tileResolution, tileResolution, tileResolution);
            temporalShader.SetFloat("_TemporalAlpha", cascade.TemporalAlpha);
            temporalShader.SetInt("_TemporalForceReplace", forceReplace ? 1 : 0);
            temporalShader.Dispatch(
                temporalKernel,
                Mathf.CeilToInt(tileResolution / 4f),
                Mathf.CeilToInt(tileResolution / 4f),
                Mathf.CeilToInt(tileResolution / 4f));

            int probes = tileResolution * tileResolution * tileResolution;
            temporalDispatchesThisFrame++;
            temporalTilesThisFrame++;
            temporalProbesThisFrame += probes;
            if (countAsHistoryReset)
                temporalResetTilesThisFrame++;
            computeDispatchesThisFrame++;
        }

        private void ClearCascade(RadianceCascade cascade)
        {
            ClearTextureSet(cascade, cascade.Textures);
            ClearTextureSet(cascade, cascade.DirectTextures);
            if (!cascade.HasPropagationTextures)
                return;
            ClearTextureSet(cascade, cascade.CandidateTextures);
            ClearTextureSet(cascade, cascade.PropagationScratchTexturesA);
            ClearTextureSet(cascade, cascade.PropagationScratchTexturesB);
        }

        private void ClearTextureSet(RadianceCascade cascade, IReadOnlyList<RenderTexture> textures)
        {
            BindWriteTextures(radianceShader, clearKernel, textures);
            BindCascadeLayout(cascade);
            radianceShader.Dispatch(clearKernel,
                Mathf.CeilToInt(cascade.Resolution.x / 4f),
                Mathf.CeilToInt(cascade.Resolution.y / 4f),
                Mathf.CeilToInt(cascade.Resolution.z / 4f));
            computeDispatchesThisFrame++;
        }

        private void BindCascadeLayout(RadianceCascade cascade)
        {
            BindCascadeLayout(radianceShader, cascade);
        }

        private static void BindCascadeLayout(ComputeShader shader, RadianceCascade cascade)
        {
            Vector3Int resolution = cascade.Resolution;
            Vector3Int ring = cascade.RingOffset;
            shader.SetVector(OriginId, cascade.OriginWS);
            shader.SetVector(SizeId, cascade.SizeWS);
            shader.SetInts(ResolutionId, resolution.x, resolution.y, resolution.z);
            shader.SetInts(RingOffsetId, ring.x, ring.y, ring.z);
            shader.SetInt("_RadianceToroidal", 1);
            shader.SetInt(AvailableId, 1);
        }

        private static void BindWriteTextures(
            ComputeShader shader,
            int kernel,
            IReadOnlyList<RenderTexture> textures)
        {
            shader.SetTexture(kernel, PositiveXId, textures[0]);
            shader.SetTexture(kernel, NegativeXId, textures[1]);
            shader.SetTexture(kernel, PositiveYId, textures[2]);
            shader.SetTexture(kernel, NegativeYId, textures[3]);
            shader.SetTexture(kernel, PositiveZId, textures[4]);
            shader.SetTexture(kernel, NegativeZId, textures[5]);
        }

        private void BindReadTextures(RadianceCascade cascade, int kernel)
        {
            radianceShader.SetTexture(kernel, "_RadianceReadPositiveX", cascade.Textures[0]);
            radianceShader.SetTexture(kernel, "_RadianceReadNegativeX", cascade.Textures[1]);
            radianceShader.SetTexture(kernel, "_RadianceReadPositiveY", cascade.Textures[2]);
            radianceShader.SetTexture(kernel, "_RadianceReadNegativeY", cascade.Textures[3]);
            radianceShader.SetTexture(kernel, "_RadianceReadPositiveZ", cascade.Textures[4]);
            radianceShader.SetTexture(kernel, "_RadianceReadNegativeZ", cascade.Textures[5]);
        }

        private void BindDebugDirectTextures(RadianceCascade cascade, int kernel)
        {
            IReadOnlyList<RenderTexture> textures = ShouldPropagate(cascade)
                ? cascade.DirectTextures
                : cascade.Textures;
            for (int i = 0; i < 6; i++)
                radianceShader.SetTexture(kernel, DebugDirectTextureIds[i], textures[i]);
        }

        private void BindPropagationDirectTextures(IReadOnlyList<RenderTexture> textures)
        {
            for (int i = 0; i < 6; i++)
                propagationShader.SetTexture(propagationKernel, PropagationDirectTextureIds[i], textures[i]);
        }

        private void BindPropagationInputTextures(IReadOnlyList<RenderTexture> textures)
        {
            for (int i = 0; i < 6; i++)
                propagationShader.SetTexture(propagationKernel, PropagationInputTextureIds[i], textures[i]);
        }

        private bool ShouldPropagate(RadianceCascade cascade) =>
            enableDiffusePropagation && propagationStrength > 0f && propagationKernel >= 0 &&
            cascade.HasPropagationTextures && cascade.Index <= maximumPropagationCascadeIndex;

        private void CalculatePropagationRegion(
            RadianceCascade cascade,
            Vector3Int localTile,
            int halo,
            out Vector3Int offset,
            out Vector3Int size)
        {
            Vector3Int coreOffset = localTile * cascade.TileResolution;
            Vector3Int expansion = Vector3Int.one * Mathf.Max(0, halo);
            offset = Vector3Int.Max(Vector3Int.zero, coreOffset - expansion);
            Vector3Int maximum = Vector3Int.Min(
                cascade.Resolution,
                coreOffset + Vector3Int.one * cascade.TileResolution + expansion);
            size = maximum - offset;
        }

        private void PublishShaderGlobals()
        {
            if (!initialized)
                return;
            Shader.SetGlobalInt(ClipmapAvailableId, 1);
            Shader.SetGlobalInt(CascadeCountId, cascades.Count);
            Shader.SetGlobalFloat("_DynamicGI_RadianceCascadeBlendStart", cascadeBlendStart);
            for (int i = 0; i < MaximumCascadeCount; i++)
            {
                bool available = i < cascades.Count;
                Shader.SetGlobalInt(CascadeAvailableIds[i], available ? 1 : 0);
                if (!available)
                    continue;
                RadianceCascade cascade = cascades[i];
                for (int direction = 0; direction < 6; direction++)
                    Shader.SetGlobalTexture(CascadeTextureIds[i][direction], cascade.Textures[direction]);
                Shader.SetGlobalVector(CascadeOriginIds[i], cascade.OriginWS);
                Shader.SetGlobalVector(CascadeSizeIds[i], cascade.SizeWS);
                Shader.SetGlobalVector(CascadeResolutionIds[i], (Vector3)cascade.Resolution);
                Shader.SetGlobalVector(CascadeRingOffsetIds[i], (Vector3)cascade.RingOffset);
            }
        }

        private void DetectLightingChanges()
        {
            Vector3 direction = GetSunDirection();
            Vector3 sun = GetSunRadiance();
            Color linearSky = skyColor.linear;
            Vector3 sky = new(linearSky.r * skyIntensity, linearSky.g * skyIntensity, linearSky.b * skyIntensity);

            bool sunHasEnergy = MaxComponent(sun) > minimumSunRadianceChange ||
                                MaxComponent(lastSunRadiance) > minimumSunRadianceChange;
            float minimumDirectionDot = Mathf.Cos(minimumSunAngularChangeDegrees * Mathf.Deg2Rad);
            bool directionChanged = sunHasEnergy &&
                                    Vector3.Dot(direction, lastSunDirection) < minimumDirectionDot;
            bool sunRadianceChanged = MaxAbsDelta(sun, lastSunRadiance) > minimumSunRadianceChange;
            bool skyRadianceChanged = MaxAbsDelta(sky, lastSkyRadiance) > minimumSkyRadianceChange;
            if (!directionChanged && !sunRadianceChanged && !skyRadianceChanged)
                return;

            lastSunDirection = direction;
            lastSunRadiance = sun;
            lastSkyRadiance = sky;
            if (directionChanged || sunRadianceChanged) sunRevision++;
            lightingRefreshesThisFrame++;
            InvalidateAllCascades();
        }

        private void CaptureLightingState()
        {
            lastSunDirection = GetSunDirection();
            lastSunRadiance = GetSunRadiance();
            Color linearSky = skyColor.linear;
            lastSkyRadiance = new Vector3(linearSky.r, linearSky.g, linearSky.b) * skyIntensity;
        }

        private Vector3 ResolveTrackingPosition()
        {
            if (trackingTarget != null)
                return trackingTarget.position;
            if (Camera.main != null)
                return Camera.main.transform.position;
            return transform.position;
        }

        private Vector3 GetSunDirection() => sunLight != null ? -sunLight.transform.forward.normalized : Vector3.up;

        private Vector3 GetSunRadiance()
        {
            if (sunLight == null || !sunLight.enabled || !sunLight.gameObject.activeInHierarchy)
                return Vector3.zero;
            Color color = sunLight.color.linear;
            float intensity = Mathf.Max(0f, sunLight.intensity) * sunIntensityScale;
            intensity *= GetSunHorizonFactor(GetSunDirection());
            return new Vector3(color.r * intensity, color.g * intensity, color.b * intensity);
        }

        private float GetSunHorizonFactor(Vector3 direction)
        {
            if (!fadeSunBelowHorizon)
                return 1f;
            float fadeHeight = Mathf.Sin(sunHorizonFadeDegrees * Mathf.Deg2Rad);
            return Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(direction.y / Mathf.Max(0.0001f, fadeHeight)));
        }

        private void InvalidateAllCascades(bool resetTemporal = false)
        {
            for (int i = 0; i < cascades.Count; i++) cascades[i].InvalidateAll(resetTemporal);
        }

        private void OnEmissiveContributorChanged(
            GIEmissiveContributor _,
            EmissiveContributorChange change)
        {
            emissiveUploadDirty = true;
            emissiveRevision++;
            pendingEmissiveChanges++;
            if (!initialized)
                return;

            int lastCascade = Mathf.Min(change.MaximumCascadeIndex, cascades.Count - 1);
            for (int i = 0; i <= lastCascade; i++)
                cascades[i].InvalidateWorldBounds(ExpandForPropagation(change.InfluenceBounds, cascades[i]));
        }

        private void UploadEmissiveContributorsIfNeeded()
        {
            if (!emissiveUploadDirty || emissiveContributorBuffer == null)
                return;

            int count = 0;
            bool overflow = false;
            maximumEmissiveRange = 0f;
            foreach (GIEmissiveContributor contributor in GIEmissiveContributor.ActiveContributors)
            {
                if (contributor == null || !contributor.TryBuildGpuData(out EmissiveContributorGpuData data))
                    continue;
                if (count >= maximumEmissiveContributors)
                {
                    overflow = true;
                    continue;
                }

                emissiveUploadData[count++] = data;
                maximumEmissiveRange = Mathf.Max(maximumEmissiveRange, contributor.InfluenceRange);
            }

            if (count > 0)
                emissiveContributorBuffer.SetData(emissiveUploadData, 0, 0, count);
            activeEmissiveContributors = count;
            emissiveUploadDirty = false;

            if (overflow && !emissiveOverflowWarningIssued)
            {
                UnityEngine.Debug.LogWarning(
                    $"Radiance Clipmap supports {maximumEmissiveContributors} emissive contributors; extra sources are ignored.",
                    this);
                emissiveOverflowWarningIssued = true;
            }
            else if (!overflow)
            {
                emissiveOverflowWarningIssued = false;
            }
        }

        private static float MaxAbsDelta(Vector3 value, Vector3 previous) =>
            Mathf.Max(Mathf.Abs(value.x - previous.x),
                Mathf.Max(Mathf.Abs(value.y - previous.y), Mathf.Abs(value.z - previous.z)));

        private static float MaxComponent(Vector3 value) => Mathf.Max(value.x, Mathf.Max(value.y, value.z));

        private Bounds CalculateSunInfluence(Bounds changedBounds)
        {
            Bounds influence = changedBounds;
            influence.Encapsulate(new Bounds(changedBounds.center - GetSunDirection() * sunTraceDistance, changedBounds.size));
            influence.Expand(Vector3.one * 2f);
            return influence;
        }

        private Bounds ExpandForPropagation(Bounds bounds, RadianceCascade cascade)
        {
            if (!ShouldPropagate(cascade))
                return bounds;
            bounds.Expand(Vector3.one * (propagationIterations * cascade.ProbeSpacing * 2f));
            return bounds;
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

        private void OnGeometryReset(Bounds _) { for (int i = 0; i < cascades.Count; i++) cascades[i].InvalidateAll(true); }
        private void OnGeometryRegionRebuilt(Bounds bounds) => InvalidateRegion(CalculateSunInfluence(bounds), true);
        private void OnSkyReset(Bounds _) { for (int i = 0; i < cascades.Count; i++) cascades[i].InvalidateAll(); }
        private void OnSkyRegionUpdated(Bounds bounds) => InvalidateRegion(bounds);

        private void OnProbeReadback(AsyncGPUReadbackRequest request)
        {
            bool error = request.hasError;
            RadianceProbeGpuData value = default;
            if (!error)
            {
                var data = request.GetData<RadianceProbeGpuData>();
                error = data.Length == 0;
                if (!error) value = data[0];
            }
            Action<RadianceClipmapProbeResult> callback = pendingQueryCallback;
            RadianceProbeResult probe = new(pendingQueryPosition, value, error);
            int cascade = pendingQueryCascade;
            pendingQueryCallback = null;
            queryPending = false;
            callback?.Invoke(new RadianceClipmapProbeResult(cascade, probe));
        }

        private int FindFinestContainingCascade(Vector3 position)
        {
            for (int i = 0; i < cascades.Count; i++)
                if (cascades[i].WorldBounds.Contains(position)) return i;
            return -1;
        }

        private static Vector3Int WorldToProbeFloor(Vector3 position, RadianceCascade cascade)
        {
            Vector3 relative = (position - cascade.OriginWS) / cascade.ProbeSpacing;
            return new Vector3Int(Mathf.FloorToInt(relative.x), Mathf.FloorToInt(relative.y), Mathf.FloorToInt(relative.z));
        }

        private static Vector3Int ClampProbe(Vector3Int value, Vector3Int resolution) => new(
            Mathf.Clamp(value.x, 0, resolution.x - 1),
            Mathf.Clamp(value.y, 0, resolution.y - 1),
            Mathf.Clamp(value.z, 0, resolution.z - 1));

        private void ResetFrameStats()
        {
            updatedTilesThisFrame = 0;
            updatedProbesThisFrame = 0;
            exposedProbesThisFrame = 0;
            recycledProbesThisFrame = 0;
            originMovesThisFrame = 0;
            computeDispatchesThisFrame = 0;
            propagationDispatchesThisFrame = 0;
            propagatedProbesThisFrame = 0;
            temporalDispatchesThisFrame = 0;
            temporalTilesThisFrame = 0;
            temporalProbesThisFrame = 0;
            temporalResetTilesThisFrame = 0;
            lightingRefreshesThisFrame = 0;
            emissiveChangesThisFrame = pendingEmissiveChanges;
            pendingEmissiveChanges = 0;
            updateCpuMilliseconds = 0.0;
        }

        private static GraphicsFormat ChooseTextureFormat() =>
            SystemInfo.IsFormatSupported(GraphicsFormat.R16G16B16A16_SFloat, FormatUsage.LoadStore)
                ? GraphicsFormat.R16G16B16A16_SFloat
                : GraphicsFormat.R32G32B32A32_SFloat;

        private void ReleaseResources()
        {
            initialized = false;
            queryPending = false;
            pendingQueryCallback = null;
            recentRegions.Clear();
            processedTiles.Clear();
            for (int i = 0; i < cascades.Count; i++) cascades[i].Dispose();
            cascades.Clear();
            queryPositionBuffer?.Dispose();
            queryResultBuffer?.Dispose();
            emissiveContributorBuffer?.Dispose();
            queryPositionBuffer = null;
            queryResultBuffer = null;
            emissiveContributorBuffer = null;
            emissiveUploadData = Array.Empty<EmissiveContributorGpuData>();
            activeEmissiveContributors = 0;
            maximumEmissiveRange = 0f;
            emissiveUploadDirty = true;
            propagationKernel = -1;
            temporalKernel = -1;
            debugQueryKernel = -1;
            lastDebugProbeCount = 0;
            lastDebugProbeStride = 0;
        }

        private static int[] CreateCascadeTextureIds(int index) => new[]
        {
            Shader.PropertyToID($"_DynamicGI_RadianceCascade{index}PositiveX"),
            Shader.PropertyToID($"_DynamicGI_RadianceCascade{index}NegativeX"),
            Shader.PropertyToID($"_DynamicGI_RadianceCascade{index}PositiveY"),
            Shader.PropertyToID($"_DynamicGI_RadianceCascade{index}NegativeY"),
            Shader.PropertyToID($"_DynamicGI_RadianceCascade{index}PositiveZ"),
            Shader.PropertyToID($"_DynamicGI_RadianceCascade{index}NegativeZ")
        };

        private static int[] CreateCascadePropertyIds(string suffix) => new[]
        {
            Shader.PropertyToID($"_DynamicGI_RadianceCascade0{suffix}"),
            Shader.PropertyToID($"_DynamicGI_RadianceCascade1{suffix}"),
            Shader.PropertyToID($"_DynamicGI_RadianceCascade2{suffix}"),
            Shader.PropertyToID($"_DynamicGI_RadianceCascade3{suffix}")
        };

        private static int[] CreateDirectionalPropertyIds(string prefix) => new[]
        {
            Shader.PropertyToID($"{prefix}PositiveX"),
            Shader.PropertyToID($"{prefix}NegativeX"),
            Shader.PropertyToID($"{prefix}PositiveY"),
            Shader.PropertyToID($"{prefix}NegativeY"),
            Shader.PropertyToID($"{prefix}PositiveZ"),
            Shader.PropertyToID($"{prefix}NegativeZ")
        };

        [System.Diagnostics.Conditional("UNITY_EDITOR")]
        private void TryAssignDefaultComputeShader()
        {
#if UNITY_EDITOR
            if (radianceShader == null)
                radianceShader = UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>(DefaultComputePath);
            if (propagationShader == null)
                propagationShader = UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>(DefaultPropagationComputePath);
            if (temporalShader == null)
                temporalShader = UnityEditor.AssetDatabase.LoadAssetAtPath<ComputeShader>(DefaultTemporalComputePath);
#endif
        }

        private readonly struct ProcessedTile
        {
            public readonly Vector3Int GlobalTile;
            public readonly Vector3Int LocalTile;
            public readonly bool ResetTemporal;

            public ProcessedTile(Vector3Int globalTile, Vector3Int localTile, bool resetTemporal)
            {
                GlobalTile = globalTile;
                LocalTile = localTile;
                ResetTemporal = resetTemporal;
            }
        }

        private readonly struct RecentRegion
        {
            public readonly int CascadeIndex;
            public readonly Bounds Bounds;
            public readonly int ExpiryFrame;

            public RecentRegion(int cascadeIndex, Bounds bounds, int expiryFrame)
            {
                CascadeIndex = cascadeIndex;
                Bounds = bounds;
                ExpiryFrame = expiryFrame;
            }
        }
    }
}
