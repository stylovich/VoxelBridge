using System;
using System.Collections.Generic;
using DynamicGI.Radiance;
using UnityEngine;
using UnityEngine.Rendering;

namespace DynamicGI.Debugging
{
    /// <summary>GPU and Scene-view inspection for toroidal radiance cascades.</summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public sealed class RadianceClipmapDebug : MonoBehaviour
    {
        private static readonly int SamplesId = Shader.PropertyToID("_DynamicGIRadianceDebugSamples");
        private static readonly int ScaleId = Shader.PropertyToID("_DynamicGIRadianceDebugScale");
        private static readonly Color[] CascadeColors =
        {
            new(1f, 0.15f, 0.65f, 0.9f),
            new(0.15f, 0.8f, 1f, 0.9f),
            new(0.35f, 1f, 0.25f, 0.9f),
            new(1f, 0.72f, 0.08f, 0.9f)
        };

        [SerializeField] private WorldRadianceClipmap radianceClipmap;
        [SerializeField] private Transform debugCenterTransform;
        [SerializeField] private Transform probeQueryTarget;

        [Header("Cascades")]
        [SerializeField, Range(0, 3)] private int selectedCascade;
        [SerializeField] private bool showAllCascadeBounds = true;
        [SerializeField] private bool showDirtyTiles = true;
        [SerializeField] private bool showRecentlyUpdatedTiles = true;
        [SerializeField] private bool showRingOffsetsAndStats = true;
        [SerializeField] private bool showTrackingTarget = true;

        [Header("Selected cascade probes")]
        [SerializeField] private bool showRadianceProbes = true;
        [SerializeField] private RadianceDebugDirection displayedDirection = RadianceDebugDirection.Average;
        [SerializeField] private RadianceDebugSource displayedSource = RadianceDebugSource.Resolved;
        [SerializeField, Min(0f)] private float debugRadius;
        [SerializeField, Range(256, 65536)] private int maximumProbeInstances = 8192;
        [SerializeField, Range(0.05f, 0.8f)] private float probeScale = 0.22f;
        [SerializeField, Min(0f)] private float exposure = 1f;
        [SerializeField] private bool automaticExposure;
        [SerializeField, Range(0.05f, 0.95f)] private float automaticExposurePercentile = 0.25f;
        [SerializeField, Range(0.05f, 1f)] private float automaticExposureTarget = 0.65f;
        [SerializeField, Min(1f)] private float maximumAutomaticExposure = 4096f;
        [SerializeField, Range(0.01f, 1f)] private float automaticExposureAdaptation = 0.35f;
        [SerializeField, Range(0f, 1f)] private float minimumAlpha = 0.15f;

        [Header("Numeric values")]
        [SerializeField] private bool showNumericValues = true;
        [SerializeField] private bool numericHorizontalSliceOnly = true;
        [SerializeField] private bool showQueryNumericSlice = true;
        [SerializeField] private bool showGroundNumericSlice = true;
        [SerializeField] private float groundSliceWorldY = 0.25f;
        [SerializeField] private Color groundSliceColor = new(1f, 0.72f, 0.08f, 1f);
        [SerializeField] private bool showCeilingNumericSlice = true;
        [SerializeField] private float ceilingSliceWorldY = 4.25f;
        [SerializeField] private Color ceilingSliceColor = new(0.72f, 0.35f, 1f, 1f);
        [SerializeField] private bool showNumericSlicePlanes = true;
        [SerializeField, Range(16, 2048)] private int maximumNumericLabels = 512;
        [SerializeField, Min(0.1f)] private float numericReadbackInterval = 0.5f;
        [SerializeField, Range(3, 6)] private int smallValueDecimalPlaces = 6;
        [SerializeField] private Color numericTextColor = Color.white;
        [SerializeField] private bool queryDetailedProbe = true;
        [SerializeField, Min(0.1f)] private float queryInterval = 0.5f;

        private readonly List<RadianceCascadeRuntimeStats> cascadeStats = new();
        private readonly List<Bounds> dirtyBounds = new();
        private readonly List<Bounds> recentBounds = new();
        private readonly IndirectArguments[] indirectArguments = new IndirectArguments[1];
        private GraphicsBuffer debugSampleBuffer;
        private GraphicsBuffer indirectArgumentsBuffer;
        private Material debugMaterial;
        private Mesh cubeMesh;
        private RadianceDebugSampleGpu[] numericSamples = Array.Empty<RadianceDebugSampleGpu>();
        private float[] automaticExposureSamples = Array.Empty<float>();
        private int numericSampleCount;
        private int querySliceSampleCount;
        private int groundSliceSampleCount;
        private int ceilingSliceSampleCount;
        private int pendingNumericCount;
        private bool numericReadbackPending;
        private bool queryPending;
        private bool hasQueryResult;
        private RadianceClipmapProbeResult lastQueryResult;
        private RadianceDebugSource pendingQuerySource;
        private RadianceDebugSource lastQuerySource;
        private RadianceDebugSource pendingNumericSource;
        private RadianceDebugSource numericSampleSource;
        private float effectiveExposure = 1f;
        private bool resourcesDirty;
        private double nextNumericTime;
        private double nextQueryTime;

#if UNITY_EDITOR
        private GUIStyle numericStyle;
        private GUIStyle detailStyle;
#endif

        public int SelectedCascade => selectedCascade;
        public int NumericSampleCount => numericSampleCount;
        public int QuerySliceSampleCount => querySliceSampleCount;
        public int GroundSliceSampleCount => groundSliceSampleCount;
        public int CeilingSliceSampleCount => ceilingSliceSampleCount;
        public bool HasQueryResult => hasQueryResult;
        public RadianceClipmapProbeResult LastQueryResult => lastQueryResult;
        public RadianceDebugSource DisplayedSource => displayedSource;
        public float EffectiveExposure => automaticExposure ? effectiveExposure : exposure;

        private void Reset() => radianceClipmap = GetComponent<WorldRadianceClipmap>();

        private void OnEnable()
        {
            radianceClipmap ??= GetComponent<WorldRadianceClipmap>();
            effectiveExposure = Mathf.Max(0f, exposure);
            CreateResources();
        }

        private void OnDisable() => ReleaseResources();

        private void OnValidate()
        {
            selectedCascade = Mathf.Clamp(selectedCascade, 0, 3);
            debugRadius = Mathf.Max(0f, debugRadius);
            maximumProbeInstances = Mathf.Clamp(maximumProbeInstances, 256, 65536);
            probeScale = Mathf.Clamp(probeScale, 0.05f, 0.8f);
            exposure = Mathf.Max(0f, exposure);
            automaticExposurePercentile = Mathf.Clamp(automaticExposurePercentile, 0.05f, 0.95f);
            automaticExposureTarget = Mathf.Clamp(automaticExposureTarget, 0.05f, 1f);
            maximumAutomaticExposure = Mathf.Max(Mathf.Max(1f, exposure), maximumAutomaticExposure);
            automaticExposureAdaptation = Mathf.Clamp(automaticExposureAdaptation, 0.01f, 1f);
            smallValueDecimalPlaces = Mathf.Clamp(smallValueDecimalPlaces, 3, 6);
            minimumAlpha = Mathf.Clamp01(minimumAlpha);
            maximumNumericLabels = Mathf.Clamp(maximumNumericLabels, 16, 2048);
            numericReadbackInterval = Mathf.Max(0.1f, numericReadbackInterval);
            queryInterval = Mathf.Max(0.1f, queryInterval);
            effectiveExposure = exposure;
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
            if (radianceClipmap == null || !radianceClipmap.IsInitialized || debugSampleBuffer == null)
                return;
            selectedCascade = Mathf.Clamp(selectedCascade, 0, Mathf.Max(0, radianceClipmap.CascadeCount - 1));
            UpdateDetailedQuery();
            if (!showRadianceProbes || debugMaterial == null || indirectArgumentsBuffer == null)
                return;

            Vector3 center = ResolveDebugCenter();
            if (!radianceClipmap.BuildDebugSamples(
                    selectedCascade,
                    debugSampleBuffer,
                    maximumProbeInstances,
                    center,
                    debugRadius,
                    displayedDirection,
                    displayedSource,
                    out int count))
            {
                return;
            }

            if (!radianceClipmap.TryGetCascade(selectedCascade, out RadianceCascade cascade))
                return;
            indirectArguments[0].InstanceCount = (uint)count;
            indirectArgumentsBuffer.SetData(indirectArguments);
            debugMaterial.SetBuffer(SamplesId, debugSampleBuffer);
            debugMaterial.SetFloat(ScaleId, cascade.ProbeSpacing * probeScale);
            debugMaterial.SetFloat("_Exposure", EffectiveExposure);
            debugMaterial.SetFloat("_MinimumAlpha", minimumAlpha);
#pragma warning disable 618
            Graphics.DrawMeshInstancedIndirect(
                cubeMesh, 0, debugMaterial, cascade.WorldBounds, indirectArgumentsBuffer,
                0, null, ShadowCastingMode.Off, false, gameObject.layer, null, LightProbeUsage.Off);
#pragma warning restore 618

            if ((showNumericValues || automaticExposure) && !numericReadbackPending &&
                Time.realtimeSinceStartupAsDouble >= nextNumericTime)
            {
                nextNumericTime = Time.realtimeSinceStartupAsDouble + numericReadbackInterval;
                pendingNumericCount = count;
                pendingNumericSource = displayedSource;
                numericReadbackPending = true;
                AsyncGPUReadback.Request(debugSampleBuffer, OnNumericReadback);
            }
        }

        private void UpdateDetailedQuery()
        {
            if (!queryDetailedProbe || queryPending || Time.realtimeSinceStartupAsDouble < nextQueryTime)
                return;
            nextQueryTime = Time.realtimeSinceStartupAsDouble + queryInterval;
            pendingQuerySource = displayedSource;
            queryPending = radianceClipmap.RequestDebugProbe(
                ResolveQueryPosition(),
                pendingQuerySource,
                OnQueryCompleted);
        }

        private void OnQueryCompleted(RadianceClipmapProbeResult result)
        {
            queryPending = false;
            hasQueryResult = true;
            lastQueryResult = result;
            lastQuerySource = pendingQuerySource;
        }

        private void OnNumericReadback(AsyncGPUReadbackRequest request)
        {
            numericReadbackPending = false;
            if (request.hasError)
            {
                numericSampleCount = 0;
                querySliceSampleCount = 0;
                groundSliceSampleCount = 0;
                ceilingSliceSampleCount = 0;
                return;
            }
            var source = request.GetData<RadianceDebugSampleGpu>();
            int count = Mathf.Min(pendingNumericCount, source.Length);
            if (numericSamples.Length < count)
                numericSamples = new RadianceDebugSampleGpu[count];
            for (int i = 0; i < count; i++) numericSamples[i] = source[i];
            numericSampleCount = count;
            numericSampleSource = pendingNumericSource;
            UpdateAutomaticExposure();
            UpdateNumericSliceCounts();
        }

        private void UpdateAutomaticExposure()
        {
            if (!automaticExposure)
            {
                effectiveExposure = exposure;
                return;
            }

            if (automaticExposureSamples.Length < numericSampleCount)
                automaticExposureSamples = new float[numericSampleCount];
            int validCount = 0;
            for (int i = 0; i < numericSampleCount; i++)
            {
                RadianceDebugSampleGpu sample = numericSamples[i];
                float luminance = sample.PositionAndLuminance.w;
                if (sample.ColorAndValidity.w <= 0.001f || luminance <= 0.0000001f ||
                    float.IsNaN(luminance) || float.IsInfinity(luminance))
                {
                    continue;
                }
                automaticExposureSamples[validCount++] = luminance;
            }

            float targetExposure = exposure;
            if (validCount > 0)
            {
                Array.Sort(automaticExposureSamples, 0, validCount);
                int percentileIndex = Mathf.Clamp(
                    Mathf.RoundToInt((validCount - 1) * automaticExposurePercentile),
                    0,
                    validCount - 1);
                float referenceLuminance = automaticExposureSamples[percentileIndex];
                targetExposure = automaticExposureTarget / Mathf.Max(0.0000001f, referenceLuminance);
            }
            targetExposure = Mathf.Clamp(targetExposure, exposure, maximumAutomaticExposure);
            effectiveExposure = Mathf.Lerp(
                Mathf.Max(exposure, effectiveExposure),
                targetExposure,
                automaticExposureAdaptation);
        }

        private Vector3 ResolveDebugCenter()
        {
            if (debugCenterTransform != null) return debugCenterTransform.position;
            if (radianceClipmap != null && radianceClipmap.TrackingTarget != null)
                return radianceClipmap.TrackingTarget.position;
            if (radianceClipmap != null && radianceClipmap.TryGetCascade(selectedCascade, out RadianceCascade cascade))
                return cascade.WorldBounds.center;
            return transform.position;
        }

        private Vector3 ResolveQueryPosition() => probeQueryTarget != null ? probeQueryTarget.position : ResolveDebugCenter();

        private void UpdateNumericSliceCounts()
        {
            querySliceSampleCount = 0;
            groundSliceSampleCount = 0;
            ceilingSliceSampleCount = 0;
            if (radianceClipmap == null ||
                !radianceClipmap.TryGetCascade(selectedCascade, out RadianceCascade cascade))
            {
                return;
            }

            for (int i = 0; i < numericSampleCount; i++)
            {
                RadianceDebugSampleGpu sample = numericSamples[i];
                if (sample.ColorAndValidity.w <= 0.001f)
                    continue;
                switch (ClassifyNumericSlice(sample.PositionAndLuminance.y, cascade))
                {
                    case NumericSliceKind.Query: querySliceSampleCount++; break;
                    case NumericSliceKind.Ground: groundSliceSampleCount++; break;
                    case NumericSliceKind.Ceiling: ceilingSliceSampleCount++; break;
                }
            }
        }

        private NumericSliceKind ClassifyNumericSlice(float worldY, RadianceCascade cascade)
        {
            float tolerance = cascade.ProbeSpacing * 0.55f;
            if (showQueryNumericSlice && Mathf.Abs(worldY - ResolveQueryPosition().y) <= tolerance)
                return NumericSliceKind.Query;
            if (showGroundNumericSlice && Mathf.Abs(worldY - groundSliceWorldY) <= tolerance)
                return NumericSliceKind.Ground;
            if (showCeilingNumericSlice && Mathf.Abs(worldY - ceilingSliceWorldY) <= tolerance)
                return NumericSliceKind.Ceiling;
            return NumericSliceKind.None;
        }

        private void CreateResources()
        {
            if (debugSampleBuffer != null)
                return;
            Shader shader = Shader.Find("Hidden/DynamicGI/RadianceDebug");
            if (shader == null)
                return;
            cubeMesh = CreateCubeMesh();
            debugMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            debugSampleBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, maximumProbeInstances, RadianceDebugSampleGpu.Stride);
            indirectArgumentsBuffer = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments, 1, sizeof(uint) * 5);
            indirectArguments[0] = new IndirectArguments
            {
                IndexCountPerInstance = cubeMesh.GetIndexCount(0),
                StartIndex = cubeMesh.GetIndexStart(0),
                BaseVertex = cubeMesh.GetBaseVertex(0)
            };
            indirectArgumentsBuffer.SetData(indirectArguments);
        }

        private void ReleaseResources()
        {
            debugSampleBuffer?.Dispose();
            indirectArgumentsBuffer?.Dispose();
            debugSampleBuffer = null;
            indirectArgumentsBuffer = null;
            numericReadbackPending = false;
            queryPending = false;
            hasQueryResult = false;
            numericSampleCount = 0;
            querySliceSampleCount = 0;
            groundSliceSampleCount = 0;
            ceilingSliceSampleCount = 0;
            if (debugMaterial != null)
            {
                if (Application.isPlaying) Destroy(debugMaterial); else DestroyImmediate(debugMaterial);
                debugMaterial = null;
            }
            if (cubeMesh != null)
            {
                if (Application.isPlaying) Destroy(cubeMesh); else DestroyImmediate(cubeMesh);
                cubeMesh = null;
            }
        }

        private void OnDrawGizmos()
        {
            radianceClipmap ??= GetComponent<WorldRadianceClipmap>();
            if (radianceClipmap == null)
                return;
            radianceClipmap.GetCascadeStats(cascadeStats);
            if (cascadeStats.Count == 0)
                return;
            selectedCascade = Mathf.Clamp(selectedCascade, 0, cascadeStats.Count - 1);

            if (showAllCascadeBounds)
            {
                for (int i = cascadeStats.Count - 1; i >= 0; i--)
                {
                    Gizmos.color = CascadeColors[i];
                    Gizmos.DrawWireCube(cascadeStats[i].Bounds.center, cascadeStats[i].Bounds.size);
                }
            }
            if (showDirtyTiles)
            {
                radianceClipmap.GetDirtyTileBounds(selectedCascade, dirtyBounds);
                Gizmos.color = new Color(1f, 0.08f, 0.08f, 0.72f);
                for (int i = 0; i < dirtyBounds.Count; i++)
                    Gizmos.DrawWireCube(dirtyBounds[i].center, dirtyBounds[i].size * 0.92f);
            }
            if (showRecentlyUpdatedTiles)
            {
                radianceClipmap.GetRecentUpdatedRegions(selectedCascade, recentBounds);
                Gizmos.color = new Color(1f, 0.65f, 0.05f, 0.7f);
                for (int i = 0; i < recentBounds.Count; i++)
                    Gizmos.DrawWireCube(recentBounds[i].center, recentBounds[i].size * 0.82f);
            }
            if (showTrackingTarget)
            {
                Gizmos.color = Color.white;
                Gizmos.DrawWireSphere(ResolveDebugCenter(), 0.2f);
            }
            if (showNumericValues && numericHorizontalSliceOnly && showNumericSlicePlanes)
                DrawNumericSlicePlanes(cascadeStats[selectedCascade].Bounds);

#if UNITY_EDITOR
            EnsureStyles();
            DrawNumericLabels();
            DrawDetailedQuery();
            if (showRingOffsetsAndStats) DrawCascadeStats();
#endif
        }

        private void DrawNumericSlicePlanes(Bounds bounds)
        {
            if (showQueryNumericSlice)
                DrawNumericSlicePlane(bounds, ResolveQueryPosition().y, numericTextColor, "CONSULTA");
            if (showGroundNumericSlice)
                DrawNumericSlicePlane(bounds, groundSliceWorldY, groundSliceColor, "SUELO");
            if (showCeilingNumericSlice)
                DrawNumericSlicePlane(bounds, ceilingSliceWorldY, ceilingSliceColor, "TECHO");
        }

        private void DrawNumericSlicePlane(Bounds bounds, float worldY, Color color, string label)
        {
            if (worldY < bounds.min.y || worldY > bounds.max.y)
                return;
            Vector3 center = new(bounds.center.x, worldY, bounds.center.z);
            Gizmos.color = new Color(color.r, color.g, color.b, 0.65f);
            Gizmos.DrawWireCube(center, new Vector3(bounds.size.x, 0.01f, bounds.size.z));
#if UNITY_EDITOR
            UnityEditor.Handles.Label(
                new Vector3(bounds.min.x, worldY, bounds.min.z),
                $"{label} y={worldY:0.00} | {SourceLabel(displayedSource)} {DirectionLabel(displayedDirection)}");
#endif
        }

#if UNITY_EDITOR
        private void DrawNumericLabels()
        {
            if (!showNumericValues || numericSampleCount == 0 || numericSampleSource != displayedSource ||
                !radianceClipmap.TryGetCascade(selectedCascade, out RadianceCascade cascade))
            {
                return;
            }
            int eligible = 0;
            for (int i = 0; i < numericSampleCount; i++) if (IsEligible(numericSamples[i], cascade)) eligible++;
            int stride = Mathf.Max(1, Mathf.CeilToInt(eligible / (float)maximumNumericLabels));
            int ordinal = 0;
            for (int i = 0; i < numericSampleCount; i++)
            {
                RadianceDebugSampleGpu sample = numericSamples[i];
                if (!IsEligible(sample, cascade) || ordinal++ % stride != 0)
                    continue;
                NumericSliceKind slice = ClassifyNumericSlice(sample.PositionAndLuminance.y, cascade);
                numericStyle.normal.textColor = SliceColor(slice);
                UnityEditor.Handles.Label(
                    ToVector3(sample.PositionAndLuminance),
                    FormatLuminance(sample.PositionAndLuminance.w, slice, numericSampleSource),
                    numericStyle);
            }
            numericStyle.normal.textColor = numericTextColor;
        }

        private bool IsEligible(RadianceDebugSampleGpu sample, RadianceCascade cascade)
        {
            if (sample.ColorAndValidity.w <= 0.001f)
                return false;
            if (!numericHorizontalSliceOnly)
                return true;
            return ClassifyNumericSlice(sample.PositionAndLuminance.y, cascade) != NumericSliceKind.None;
        }

        private Color SliceColor(NumericSliceKind slice)
        {
            return slice switch
            {
                NumericSliceKind.Ground => groundSliceColor,
                NumericSliceKind.Ceiling => ceilingSliceColor,
                _ => numericTextColor
            };
        }

        private void DrawDetailedQuery()
        {
            if (!queryDetailedProbe || !hasQueryResult)
                return;
            RadianceProbeResult probe = lastQueryResult.Probe;
            RadianceProbeGpuData value = probe.Radiance;
            bool highPrecision = lastQuerySource == RadianceDebugSource.PropagationDelta;
            string text = probe.HasError
                ? "Clipmap query: ERROR"
                : $"{SourceLabel(lastQuerySource)} | Cascade {lastQueryResult.CascadeIndex} @ " +
                  $"({probe.WorldPosition.x:0.0},{probe.WorldPosition.y:0.0},{probe.WorldPosition.z:0.0})\n" +
                  $"L promedio {FormatLuminance(probe.AverageLuminance, NumericSliceKind.Query, lastQuerySource)}\n" +
                  $"+X {FormatRgb(value.PositiveX, highPrecision)}  -X {FormatRgb(value.NegativeX, highPrecision)}\n" +
                  $"+Y {FormatRgb(value.PositiveY, highPrecision)}  -Y {FormatRgb(value.NegativeY, highPrecision)}\n" +
                  $"+Z {FormatRgb(value.PositiveZ, highPrecision)}  -Z {FormatRgb(value.NegativeZ, highPrecision)}";
            UnityEditor.Handles.Label(probe.WorldPosition + Vector3.up * 0.35f, text, detailStyle);
        }

        private void DrawCascadeStats()
        {
            RadianceClipmapStats total = radianceClipmap.Stats;
            for (int i = 0; i < cascadeStats.Count; i++)
            {
                RadianceCascadeRuntimeStats stats = cascadeStats[i];
                detailStyle.normal.textColor = CascadeColors[i];
                UnityEditor.Handles.Label(
                    stats.Bounds.min,
                    $"C{i} {stats.Name} | {stats.Resolution.x}x{stats.Resolution.y}x{stats.Resolution.z} @ {stats.Spacing:0.##}m\n" +
                    $"ring {stats.RingOffset} | dirty {stats.DirtyTiles}/{stats.TotalTiles} | temporal {stats.PendingTemporalTiles}\n" +
                    $"cadence {stats.UpdateIntervalFrames}f | alpha {stats.TemporalAlpha:0.00} x{stats.TemporalConvergenceSteps}\n" +
                    $"exposed {stats.ExposedProbes} | recycled {stats.RecycledProbes} | {FormatBytes(stats.EstimatedGpuBytes)}",
                    detailStyle);
            }
            detailStyle.normal.textColor = Color.white;
            UnityEditor.Handles.Label(
                cascadeStats[0].Bounds.max,
                $"Clipmap total: {total.ActiveProbes} probes | dirty {total.DirtyTiles}\n" +
                $"updated {total.UpdatedProbesThisFrame} | moves {total.OriginMovesThisFrame}\n" +
                $"sun rev {total.SunRevision} | horizon {radianceClipmap.CurrentSunHorizonFactor:0.00} | refresh {total.LightingRefreshesThisFrame}\n" +
                $"emissives {total.ActiveEmissiveContributors} | rev {total.EmissiveRevision} | changes {total.EmissiveChangesThisFrame}\n" +
                $"propagation {(total.PropagationEnabled ? $"{total.PropagationIterations}x @ {radianceClipmap.PropagationStrength:0.00}" : "off")} | " +
                $"dispatch {total.PropagationDispatchesThisFrame} | writes {total.PropagatedProbesThisFrame}\n" +
                $"temporal {(total.TemporalAccumulationEnabled ? "on" : "off")} | pending {total.PendingTemporalTiles} | " +
                $"tiles/reset {total.TemporalTilesThisFrame}/{total.TemporalResetTilesThisFrame} | writes {total.TemporalProbesThisFrame}\n" +
                $"debug {SourceLabel(displayedSource)} {DirectionLabel(displayedDirection)} | " +
                $"exposure {(automaticExposure ? $"auto {EffectiveExposure:0.#}x P{automaticExposurePercentile * 100f:0}" : $"{exposure:0.#}x")}\n" +
                $"slices Q/G/C {querySliceSampleCount}/{groundSliceSampleCount}/{ceilingSliceSampleCount}\n" +
                $"GPU {FormatBytes(total.EstimatedGpuBytes)} | CPU {total.UpdateCpuMilliseconds:0.###} ms",
                detailStyle);
        }

        private void EnsureStyles()
        {
            numericStyle ??= new GUIStyle(UnityEditor.EditorStyles.miniLabel) { alignment = TextAnchor.MiddleCenter, fontSize = 9 };
            numericStyle.normal.textColor = numericTextColor;
            detailStyle ??= new GUIStyle(UnityEditor.EditorStyles.helpBox) { fontSize = 10 };
            detailStyle.normal.textColor = Color.white;
        }

        private string FormatLuminance(
            float value,
            NumericSliceKind slice,
            RadianceDebugSource source)
        {
            bool highPrecision = source == RadianceDebugSource.PropagationDelta || slice == NumericSliceKind.Ceiling;
            return value.ToString(highPrecision ? DecimalFormat(smallValueDecimalPlaces) : "0.000");
        }

        private string FormatRgb(Vector4 value, bool highPrecision)
        {
            string format = highPrecision ? DecimalFormat(smallValueDecimalPlaces) : "0.00";
            return $"({value.x.ToString(format)},{value.y.ToString(format)},{value.z.ToString(format)})";
        }

        private static string DecimalFormat(int places) => places switch
        {
            3 => "0.000",
            4 => "0.0000",
            5 => "0.00000",
            _ => "0.000000"
        };

        private static string SourceLabel(RadianceDebugSource source) => source switch
        {
            RadianceDebugSource.Direct => "DIRECT",
            RadianceDebugSource.PropagationDelta => "PROPAGATION DELTA",
            _ => "RESOLVED"
        };

        private static string DirectionLabel(RadianceDebugDirection direction) => direction switch
        {
            RadianceDebugDirection.PositiveX => "+X",
            RadianceDebugDirection.NegativeX => "-X",
            RadianceDebugDirection.PositiveY => "+Y",
            RadianceDebugDirection.NegativeY => "-Y",
            RadianceDebugDirection.PositiveZ => "+Z",
            RadianceDebugDirection.NegativeZ => "-Z",
            RadianceDebugDirection.Maximum => "MAX",
            _ => "AVG"
        };
        private static string FormatBytes(long bytes) => bytes >= 1024L * 1024L
            ? $"{bytes / (1024f * 1024f):0.0} MiB"
            : $"{bytes / 1024f:0.0} KiB";
#endif

        private static Vector3 ToVector3(Vector4 value) => new(value.x, value.y, value.z);

        private static Mesh CreateCubeMesh()
        {
            Mesh mesh = new() { name = "Dynamic GI Clipmap Debug Probe", hideFlags = HideFlags.HideAndDontSave };
            mesh.vertices = new[]
            {
                new Vector3(-0.5f,-0.5f,-0.5f), new Vector3(0.5f,-0.5f,-0.5f),
                new Vector3(0.5f,0.5f,-0.5f), new Vector3(-0.5f,0.5f,-0.5f),
                new Vector3(-0.5f,-0.5f,0.5f), new Vector3(0.5f,-0.5f,0.5f),
                new Vector3(0.5f,0.5f,0.5f), new Vector3(-0.5f,0.5f,0.5f)
            };
            mesh.triangles = new[]
            {
                0,2,1,0,3,2, 1,2,6,1,6,5, 5,6,7,5,7,4,
                4,7,3,4,3,0, 3,7,6,3,6,2, 4,0,1,4,1,5
            };
            mesh.RecalculateBounds();
            mesh.UploadMeshData(true);
            return mesh;
        }

        private struct IndirectArguments
        {
            public uint IndexCountPerInstance;
            public uint InstanceCount;
            public uint StartIndex;
            public uint BaseVertex;
            public uint StartInstance;
        }

        private enum NumericSliceKind
        {
            None,
            Query,
            Ground,
            Ceiling
        }
    }
}
