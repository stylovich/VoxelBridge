using System;
using System.Collections.Generic;
using DynamicGI.Radiance;
using UnityEngine;
using UnityEngine.Rendering;

namespace DynamicGI.Debugging
{
    /// <summary>
    /// Visualizes Phase 4 probes as exposure-mapped GPU cubes. Optional asynchronous
    /// readback supplies numeric Scene-view labels without keeping a CPU radiance copy.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    public sealed class RadianceFieldDebug : MonoBehaviour
    {
        private static readonly int SamplesId = Shader.PropertyToID("_DynamicGIRadianceDebugSamples");
        private static readonly int ScaleId = Shader.PropertyToID("_DynamicGIRadianceDebugScale");

        [SerializeField] private WorldRadianceField radianceField;
        [SerializeField] private Camera targetCamera;
        [SerializeField] private Transform debugCenterTransform;
        [SerializeField] private Transform probeQueryTarget;

        [Header("Gizmos")]
        [SerializeField] private bool showFieldBounds = true;
        [SerializeField] private bool showDirtyTiles = true;
        [SerializeField] private bool showRecentlyUpdatedTiles = true;
        [SerializeField] private bool showCameraNeighborhood = true;
        [SerializeField] private bool showProbeGizmos = true;
        [SerializeField] private bool showSunDirection = true;
        [SerializeField] private bool showResolutionAndStats = true;

        [Header("Probe visualization")]
        [SerializeField] private bool showRadianceProbes = true;
        [Tooltip("When disabled, probe cubes are restricted to the active Scene view and never contaminate gameplay cameras.")]
        [SerializeField] private bool renderInstancesInGameView;
        [SerializeField] private RadianceDebugDirection displayedDirection = RadianceDebugDirection.Average;
        [SerializeField, Min(0f)] private float cameraRadius = 7f;
        [SerializeField, Range(256, 65536)] private int maximumProbeInstances = 8192;
        [SerializeField, Range(0.05f, 0.8f)] private float probeScale = 0.22f;
        [SerializeField, Min(0f)] private float exposure = 1f;
        [SerializeField, Range(0f, 1f)] private float minimumAlpha = 0.15f;

        [Header("Numeric labels")]
        [SerializeField] private bool showNumericValues = true;
        [SerializeField, Range(16, 2048)] private int maximumNumericLabels = 256;
        [SerializeField] private bool numericHorizontalSliceOnly = true;
        [SerializeField, Min(0.1f)] private float numericReadbackInterval = 0.5f;
        [SerializeField] private Color numericTextColor = Color.white;
        [SerializeField] private bool queryDetailedProbe = true;
        [SerializeField, Min(0.1f)] private float queryInterval = 0.5f;

        private readonly List<Bounds> dirtyTileScratch = new();
        private readonly List<Bounds> recentTileScratch = new();
        private readonly IndirectArguments[] indirectArguments = new IndirectArguments[1];
        private GraphicsBuffer debugSampleBuffer;
        private GraphicsBuffer indirectArgumentsBuffer;
        private Material debugMaterial;
        private Mesh cubeMesh;
        private RadianceDebugSampleGpu[] numericSamples = Array.Empty<RadianceDebugSampleGpu>();
        private int numericSampleCount;
        private int pendingNumericSampleCount;
        private bool numericReadbackPending;
        private bool queryPending;
        private bool resourcesDirty;
        private double nextNumericReadbackTime;
        private double nextQueryTime;
        private bool hasQueryResult;
        private RadianceProbeResult lastQueryResult;

#if UNITY_EDITOR
        private GUIStyle numericLabelStyle;
        private GUIStyle detailLabelStyle;
#endif

        public WorldRadianceField RadianceField => radianceField;
        public bool HasQueryResult => hasQueryResult;
        public RadianceProbeResult LastQueryResult => lastQueryResult;
        public int NumericSampleCount => numericSampleCount;

        private void Reset()
        {
            radianceField = GetComponent<WorldRadianceField>();
        }

        private void OnEnable()
        {
            radianceField ??= GetComponent<WorldRadianceField>();
            CreateResources();
        }

        private void OnDisable()
        {
            ReleaseResources();
        }

        private void OnValidate()
        {
            cameraRadius = Mathf.Max(0f, cameraRadius);
            maximumProbeInstances = Mathf.Clamp(maximumProbeInstances, 256, 65536);
            probeScale = Mathf.Clamp(probeScale, 0.05f, 0.8f);
            exposure = Mathf.Max(0f, exposure);
            minimumAlpha = Mathf.Clamp01(minimumAlpha);
            maximumNumericLabels = Mathf.Clamp(maximumNumericLabels, 16, 2048);
            numericReadbackInterval = Mathf.Max(0.1f, numericReadbackInterval);
            queryInterval = Mathf.Max(0.1f, queryInterval);
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

            if (radianceField == null || !radianceField.IsInitialized || debugSampleBuffer == null)
                return;

            Vector3 center = ResolveDebugCenter();
            UpdateDetailedQuery();
            bool needsSamples = showRadianceProbes || showNumericValues;
            if (!needsSamples)
                return;

            if (!radianceField.BuildDebugSamples(
                    debugSampleBuffer,
                    maximumProbeInstances,
                    center,
                    cameraRadius,
                    displayedDirection,
                    out int sampleCount))
            {
                return;
            }

            if (showRadianceProbes && debugMaterial != null && indirectArgumentsBuffer != null &&
                DynamicGIDebugRenderUtility.TryResolveCamera(renderInstancesInGameView, out Camera debugCamera))
            {
                indirectArguments[0].InstanceCount = (uint)sampleCount;
                indirectArgumentsBuffer.SetData(indirectArguments);
                debugMaterial.SetBuffer(SamplesId, debugSampleBuffer);
                debugMaterial.SetFloat(ScaleId, radianceField.ProbeSpacing * probeScale);
                debugMaterial.SetFloat("_Exposure", exposure);
                debugMaterial.SetFloat("_MinimumAlpha", minimumAlpha);
#pragma warning disable 618
                Graphics.DrawMeshInstancedIndirect(
                    cubeMesh,
                    0,
                    debugMaterial,
                    radianceField.FieldBounds,
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

            if (showNumericValues && !numericReadbackPending && Time.realtimeSinceStartupAsDouble >= nextNumericReadbackTime)
            {
                nextNumericReadbackTime = Time.realtimeSinceStartupAsDouble + numericReadbackInterval;
                pendingNumericSampleCount = sampleCount;
                numericReadbackPending = true;
                AsyncGPUReadback.Request(debugSampleBuffer, OnNumericReadback);
            }
        }

        private void UpdateDetailedQuery()
        {
            if (!queryDetailedProbe || queryPending || Time.realtimeSinceStartupAsDouble < nextQueryTime)
                return;
            nextQueryTime = Time.realtimeSinceStartupAsDouble + queryInterval;
            queryPending = radianceField.RequestProbe(ResolveQueryPosition(), OnQueryCompleted);
        }

        private void OnQueryCompleted(RadianceProbeResult result)
        {
            queryPending = false;
            hasQueryResult = true;
            lastQueryResult = result;
        }

        private void OnNumericReadback(AsyncGPUReadbackRequest request)
        {
            numericReadbackPending = false;
            if (request.hasError)
            {
                numericSampleCount = 0;
                return;
            }

            var source = request.GetData<RadianceDebugSampleGpu>();
            int count = Mathf.Min(pendingNumericSampleCount, source.Length);
            if (numericSamples.Length < count)
                numericSamples = new RadianceDebugSampleGpu[count];
            for (int i = 0; i < count; i++)
                numericSamples[i] = source[i];
            numericSampleCount = count;
        }

        private Vector3 ResolveDebugCenter()
        {
            if (debugCenterTransform != null)
                return debugCenterTransform.position;
            if (targetCamera != null)
                return targetCamera.transform.position;
            if (Application.isPlaying && Camera.main != null)
                return Camera.main.transform.position;
#if UNITY_EDITOR
            UnityEditor.SceneView sceneView = UnityEditor.SceneView.lastActiveSceneView;
            if (sceneView != null && sceneView.camera != null)
                return sceneView.camera.transform.position;
#endif
            return radianceField != null ? radianceField.FieldBounds.center : transform.position;
        }

        private Vector3 ResolveQueryPosition() => probeQueryTarget != null ? probeQueryTarget.position : ResolveDebugCenter();

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
            numericReadbackPending = false;
            queryPending = false;
            numericSampleCount = 0;

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
            radianceField ??= GetComponent<WorldRadianceField>();
            if (radianceField == null)
                return;

            if (showFieldBounds)
            {
                Gizmos.color = new Color(1f, 0.2f, 0.75f, 0.9f);
                Gizmos.DrawWireCube(radianceField.FieldBounds.center, radianceField.FieldBounds.size);
            }
            if (showDirtyTiles)
            {
                radianceField.GetDirtyTileBounds(dirtyTileScratch);
                Gizmos.color = new Color(1f, 0.12f, 0.08f, 0.7f);
                for (int i = 0; i < dirtyTileScratch.Count; i++)
                    Gizmos.DrawWireCube(dirtyTileScratch[i].center, dirtyTileScratch[i].size * 0.94f);
            }
            if (showRecentlyUpdatedTiles)
            {
                radianceField.GetRecentUpdatedRegions(recentTileScratch);
                Gizmos.color = new Color(1f, 0.65f, 0.05f, 0.7f);
                for (int i = 0; i < recentTileScratch.Count; i++)
                    Gizmos.DrawWireCube(recentTileScratch[i].center, recentTileScratch[i].size * 0.88f);
            }

            Vector3 center = ResolveDebugCenter();
            if (showCameraNeighborhood && cameraRadius > 0f)
            {
                Gizmos.color = new Color(0.85f, 0.2f, 1f, 0.6f);
                Gizmos.DrawWireSphere(center, cameraRadius);
            }

            DrawNumericProbeGizmos();

#if UNITY_EDITOR
            EnsureLabelStyles();
            if (showSunDirection && radianceField.SunLight != null)
            {
                Vector3 start = radianceField.FieldBounds.center;
                Vector3 toSun = -radianceField.SunLight.transform.forward.normalized;
                UnityEditor.Handles.color = new Color(1f, 0.75f, 0.1f, 0.95f);
                UnityEditor.Handles.ArrowHandleCap(0, start, Quaternion.LookRotation(toSun), 2f, EventType.Repaint);
                UnityEditor.Handles.Label(start + toSun * 2.2f, "hacia el Sol", numericLabelStyle);
            }
            DrawNumericLabels();
            DrawDetailedQueryLabel();
            DrawStatsLabel();
#endif
        }

        private void DrawNumericProbeGizmos()
        {
            if (!showProbeGizmos || !showNumericValues || numericSampleCount == 0)
                return;
            int stride = CalculateNumericLabelStride();
            int eligibleIndex = 0;
            Gizmos.color = new Color(1f, 1f, 1f, 0.22f);
            float size = radianceField.ProbeSpacing * 0.3f;
            for (int i = 0; i < numericSampleCount; i++)
            {
                RadianceDebugSampleGpu sample = numericSamples[i];
                if (!IsNumericSampleEligible(sample))
                    continue;
                if (eligibleIndex++ % stride == 0)
                    Gizmos.DrawWireCube(ToVector3(sample.PositionAndLuminance), Vector3.one * size);
            }
        }

        private int CalculateNumericLabelStride()
        {
            int eligibleCount = 0;
            for (int i = 0; i < numericSampleCount; i++)
            {
                if (IsNumericSampleEligible(numericSamples[i]))
                    eligibleCount++;
            }
            return Mathf.Max(1, Mathf.CeilToInt(eligibleCount / (float)maximumNumericLabels));
        }

        private bool IsNumericSampleEligible(RadianceDebugSampleGpu sample)
        {
            if (sample.ColorAndValidity.w <= 0.001f)
                return false;
            if (!numericHorizontalSliceOnly)
                return true;
            float sliceHeight = probeQueryTarget != null ? probeQueryTarget.position.y : ResolveDebugCenter().y;
            return Mathf.Abs(sample.PositionAndLuminance.y - sliceHeight) <= radianceField.ProbeSpacing * 0.55f;
        }

#if UNITY_EDITOR
        private void DrawNumericLabels()
        {
            if (!showNumericValues || numericSampleCount == 0)
                return;
            EnsureLabelStyles();
            int stride = CalculateNumericLabelStride();
            int eligibleIndex = 0;
            for (int i = 0; i < numericSampleCount; i++)
            {
                RadianceDebugSampleGpu sample = numericSamples[i];
                if (!IsNumericSampleEligible(sample) || eligibleIndex++ % stride != 0)
                    continue;
                UnityEditor.Handles.Label(
                    ToVector3(sample.PositionAndLuminance),
                    sample.PositionAndLuminance.w.ToString("0.000"),
                    numericLabelStyle);
            }
        }

        private void DrawDetailedQueryLabel()
        {
            if (!queryDetailedProbe || !hasQueryResult)
                return;
            RadianceProbeGpuData value = lastQueryResult.Radiance;
            string text = lastQueryResult.HasError
                ? "Radiance query: ERROR"
                : $"Probe @ {FormatPosition(lastQueryResult.WorldPosition)}\n" +
                  $"promedio L: {lastQueryResult.AverageLuminance:0.000}\n" +
                  $"+X {FormatRgb(value.PositiveX)}   -X {FormatRgb(value.NegativeX)}\n" +
                  $"+Y {FormatRgb(value.PositiveY)}   -Y {FormatRgb(value.NegativeY)}\n" +
                  $"+Z {FormatRgb(value.PositiveZ)}   -Z {FormatRgb(value.NegativeZ)}";
            UnityEditor.Handles.Label(lastQueryResult.WorldPosition + Vector3.up * 0.35f, text, detailLabelStyle);
        }

        private void DrawStatsLabel()
        {
            if (!showResolutionAndStats)
                return;
            RadianceFieldStats stats = radianceField.Stats;
            UnityEditor.Handles.Label(
                radianceField.FieldBounds.min,
                $"Radiance Field (6 RGB)\n{stats.Resolution.x} x {stats.Resolution.y} x {stats.Resolution.z} = {stats.ProbeCount} probes\n" +
                $"tiles {stats.DirtyTiles}/{stats.TotalTiles} | updated {stats.UpdatedTilesThisFrame}\n" +
                $"debug {radianceField.LastDebugProbeCount} / stride {radianceField.LastDebugProbeStride}\n" +
                $"GPU ~{FormatBytes(stats.EstimatedGpuBytes)} | CPU {stats.UpdateCpuMilliseconds:0.###} ms",
                detailLabelStyle);
        }

        private void EnsureLabelStyles()
        {
            numericLabelStyle ??= new GUIStyle(UnityEditor.EditorStyles.miniLabel)
            {
                normal = { textColor = numericTextColor },
                alignment = TextAnchor.MiddleCenter,
                fontSize = 9
            };
            numericLabelStyle.normal.textColor = numericTextColor;
            detailLabelStyle ??= new GUIStyle(UnityEditor.EditorStyles.helpBox)
            {
                normal = { textColor = Color.white },
                fontSize = 10
            };
        }

        private static string FormatRgb(Vector4 value) => $"({value.x:0.00},{value.y:0.00},{value.z:0.00})";
        private static string FormatPosition(Vector3 value) => $"({value.x:0.0},{value.y:0.0},{value.z:0.0})";
        private static string FormatBytes(long bytes) => bytes >= 1024L * 1024L
            ? $"{bytes / (1024f * 1024f):0.0} MiB"
            : $"{bytes / 1024f:0.0} KiB";
#endif

        private static Vector3 ToVector3(Vector4 value) => new(value.x, value.y, value.z);

        private static Mesh CreateCubeMesh()
        {
            Mesh mesh = new() { name = "Dynamic GI Radiance Debug Probe", hideFlags = HideFlags.HideAndDontSave };
            mesh.vertices = new[]
            {
                new Vector3(-0.5f, -0.5f, -0.5f), new Vector3(0.5f, -0.5f, -0.5f),
                new Vector3(0.5f, 0.5f, -0.5f), new Vector3(-0.5f, 0.5f, -0.5f),
                new Vector3(-0.5f, -0.5f, 0.5f), new Vector3(0.5f, -0.5f, 0.5f),
                new Vector3(0.5f, 0.5f, 0.5f), new Vector3(-0.5f, 0.5f, 0.5f)
            };
            mesh.triangles = new[]
            {
                0, 2, 1, 0, 3, 2, 1, 2, 6, 1, 6, 5,
                5, 6, 7, 5, 7, 4, 4, 7, 3, 4, 3, 0,
                3, 7, 6, 3, 6, 2, 4, 0, 1, 4, 1, 5
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
    }
}
