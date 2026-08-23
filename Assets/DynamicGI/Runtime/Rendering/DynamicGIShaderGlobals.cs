using UnityEngine;

namespace DynamicGI.Rendering
{
    /// <summary>
    /// Publishes the small, pipeline-independent control surface consumed by
    /// IndirectLightingProvider.hlsl. Radiance and sky textures are owned by their
    /// respective fields; this component only controls how materials use them.
    /// </summary>
    [ExecuteAlways]
    [DisallowMultipleComponent]
    [AddComponentMenu("Rendering/Dynamic GI/Shader Globals")]
    public sealed class DynamicGIShaderGlobals : MonoBehaviour
    {
        private static readonly int StrengthId = Shader.PropertyToID("_DynamicGI_Strength");
        private static readonly int OcclusionStrengthId = Shader.PropertyToID("_DynamicGI_OcclusionStrength");
        private static readonly int SaturationId = Shader.PropertyToID("_DynamicGI_IndirectSaturation");
        private static readonly int IntensityId = Shader.PropertyToID("_DynamicGI_IndirectIntensity");
        private static readonly int SurfaceNormalBiasId = Shader.PropertyToID("_DynamicGI_SurfaceNormalBias");
        private static readonly int HdrpViewBiasId = Shader.PropertyToID("_DynamicGI_HDRPViewBias");
        private static readonly int ProviderModeId = Shader.PropertyToID("_DynamicGI_IndirectProviderMode");
        private static readonly int ScreenSpaceBridgeEnabledId = Shader.PropertyToID("_DynamicGI_ScreenSpaceBridgeEnabled");
        private static readonly int HdrpAlbedoWeightId = Shader.PropertyToID("_DynamicGI_HDRPAlbedoWeight");
        private static readonly int GeometryAwareSurfaceSamplingId = Shader.PropertyToID("_DynamicGI_GeometryAwareSurfaceSampling");
        private static readonly int SurfaceVisibilityMaxStepsId = Shader.PropertyToID("_DynamicGI_SurfaceVisibilityMaxSteps");
        private static readonly int ScreenSpaceReplacementDarkeningId = Shader.PropertyToID("_DynamicGI_ScreenSpaceReplacementDarkening");
        private static readonly int ReplacementDarkeningStrengthId = Shader.PropertyToID("_DynamicGI_ReplacementDarkeningStrength");

        [Header("Indirect provider")]
        [SerializeField] private IndirectLightingProviderMode providerMode = IndirectLightingProviderMode.ExistingPlusDynamic;
        [SerializeField, Min(0f)] private float dynamicGIStrength = 1f;
        [SerializeField, Range(0f, 1f)] private float occlusionStrength = 0.5f;
        [SerializeField, Range(0f, 2f)] private float indirectSaturation = 1f;
        [SerializeField, Min(0f)] private float indirectIntensity = 1f;
        [Tooltip("Moves material samples along the visible surface normal so trilinear filtering does not mix probes through thin walls, floors, or ceilings.")]
        [SerializeField, Min(0f)] private float surfaceNormalBias = 0.5f;
        [Tooltip("Rejects trilinear probe candidates hidden behind voxel geometry. This is substantially more expensive than raw filtering but prevents cross-wall leaks.")]
        [SerializeField] private bool geometryAwareSurfaceSampling = true;
        [Tooltip("Maximum Geometry Field DDA steps for each candidate probe used by a surface sample.")]
        [SerializeField, Range(4, 128)] private int surfaceVisibilityMaxSteps = 24;

        [Header("HDRP stock-material bridge")]
        [Tooltip("Adds Dynamic GI to opaque HDRP camera color before transparents. Existing Plus Dynamic stays additive; Dynamic Only can enable the approximate replacement-darkening preview below.")]
        [SerializeField] private bool screenSpaceBridgeEnabled = true;
        [Tooltip("Additional camera-facing offset used only by the stock-material HDRP bridge to keep T-junction pixels on the visible side of intersecting shells.")]
        [SerializeField, Min(0f)] private float hdrpViewBias = 0.25f;
        [Tooltip("0 supports deferred and forward opaque materials uniformly. Values above 0 tint the bridge using HDRP GBuffer0 and are intended for deferred Lit materials.")]
        [SerializeField, Range(0f, 1f)] private float hdrpAlbedoWeight;
        [Tooltip("In DynamicOnly mode, approximately removes stock HDRP ambient lighting using sky accessibility before adding Dynamic GI. This screen-space preview also attenuates direct/specular color; custom materials remain the exact replacement path.")]
        [SerializeField] private bool screenSpaceReplacementDarkening = true;
        [SerializeField, Range(0f, 1f)] private float replacementDarkeningStrength = 1f;

        public static DynamicGIShaderGlobals Active { get; private set; }
        public IndirectLightingProviderMode ProviderMode => providerMode;
        public float DynamicGIStrength => dynamicGIStrength;
        public float OcclusionStrength => occlusionStrength;
        public float IndirectSaturation => indirectSaturation;
        public float IndirectIntensity => indirectIntensity;
        public float SurfaceNormalBias => surfaceNormalBias;
        public bool GeometryAwareSurfaceSampling => geometryAwareSurfaceSampling;
        public int SurfaceVisibilityMaxSteps => surfaceVisibilityMaxSteps;
        public bool ScreenSpaceBridgeEnabled => screenSpaceBridgeEnabled;
        public float HdrpViewBias => hdrpViewBias;
        public float HdrpAlbedoWeight => hdrpAlbedoWeight;
        public bool ScreenSpaceReplacementDarkening => screenSpaceReplacementDarkening;
        public float ReplacementDarkeningStrength => replacementDarkeningStrength;

        public void Configure(
            IndirectLightingProviderMode mode,
            float strength,
            float ambientOcclusionStrength,
            float saturation,
            float intensity,
            float normalBias,
            float bridgeViewBias,
            bool enableScreenSpaceBridge,
            float bridgeAlbedoWeight = 0f,
            bool enableGeometryAwareSurfaceSampling = true,
            int visibilityMaxSteps = 24,
            bool enableScreenSpaceReplacementDarkening = true,
            float darkeningStrength = 1f)
        {
            providerMode = mode;
            dynamicGIStrength = Mathf.Max(0f, strength);
            occlusionStrength = Mathf.Clamp01(ambientOcclusionStrength);
            indirectSaturation = Mathf.Clamp(saturation, 0f, 2f);
            indirectIntensity = Mathf.Max(0f, intensity);
            surfaceNormalBias = Mathf.Max(0f, normalBias);
            hdrpViewBias = Mathf.Max(0f, bridgeViewBias);
            screenSpaceBridgeEnabled = enableScreenSpaceBridge;
            hdrpAlbedoWeight = Mathf.Clamp01(bridgeAlbedoWeight);
            geometryAwareSurfaceSampling = enableGeometryAwareSurfaceSampling;
            surfaceVisibilityMaxSteps = Mathf.Clamp(visibilityMaxSteps, 4, 128);
            screenSpaceReplacementDarkening = enableScreenSpaceReplacementDarkening;
            replacementDarkeningStrength = Mathf.Clamp01(darkeningStrength);
            PublishNow();
        }

        public void PublishNow()
        {
            if (!isActiveAndEnabled || Active != this)
                return;

            Shader.SetGlobalFloat(StrengthId, dynamicGIStrength);
            Shader.SetGlobalFloat(OcclusionStrengthId, occlusionStrength);
            Shader.SetGlobalFloat(SaturationId, indirectSaturation);
            Shader.SetGlobalFloat(IntensityId, indirectIntensity);
            Shader.SetGlobalFloat(SurfaceNormalBiasId, surfaceNormalBias);
            Shader.SetGlobalFloat(HdrpViewBiasId, hdrpViewBias);
            Shader.SetGlobalInt(ProviderModeId, (int)providerMode);
            Shader.SetGlobalInt(ScreenSpaceBridgeEnabledId, screenSpaceBridgeEnabled ? 1 : 0);
            Shader.SetGlobalFloat(HdrpAlbedoWeightId, hdrpAlbedoWeight);
            Shader.SetGlobalInt(GeometryAwareSurfaceSamplingId, geometryAwareSurfaceSampling ? 1 : 0);
            Shader.SetGlobalInt(SurfaceVisibilityMaxStepsId, surfaceVisibilityMaxSteps);
            Shader.SetGlobalInt(ScreenSpaceReplacementDarkeningId, screenSpaceReplacementDarkening ? 1 : 0);
            Shader.SetGlobalFloat(ReplacementDarkeningStrengthId, replacementDarkeningStrength);
        }

        private void OnEnable()
        {
            if (Active != null && Active != this)
                Debug.LogWarning("Multiple DynamicGIShaderGlobals are enabled. Shader controls use the latest one.", this);
            Active = this;
            Sanitize();
            PublishNow();
        }

        private void OnDisable()
        {
            if (Active != this)
                return;

            Active = null;
            Shader.SetGlobalFloat(StrengthId, 0f);
            Shader.SetGlobalFloat(OcclusionStrengthId, 0f);
            Shader.SetGlobalFloat(SaturationId, 1f);
            Shader.SetGlobalFloat(IntensityId, 1f);
            Shader.SetGlobalFloat(SurfaceNormalBiasId, 0f);
            Shader.SetGlobalFloat(HdrpViewBiasId, 0f);
            Shader.SetGlobalInt(ProviderModeId, (int)IndirectLightingProviderMode.ExistingOnly);
            Shader.SetGlobalInt(ScreenSpaceBridgeEnabledId, 0);
            Shader.SetGlobalFloat(HdrpAlbedoWeightId, 0f);
            Shader.SetGlobalInt(GeometryAwareSurfaceSamplingId, 0);
            Shader.SetGlobalInt(SurfaceVisibilityMaxStepsId, 24);
            Shader.SetGlobalInt(ScreenSpaceReplacementDarkeningId, 0);
            Shader.SetGlobalFloat(ReplacementDarkeningStrengthId, 0f);
        }

        private void OnValidate()
        {
            Sanitize();
            PublishNow();
        }

        private void LateUpdate() => PublishNow();

        private void Sanitize()
        {
            dynamicGIStrength = Mathf.Max(0f, dynamicGIStrength);
            occlusionStrength = Mathf.Clamp01(occlusionStrength);
            indirectSaturation = Mathf.Clamp(indirectSaturation, 0f, 2f);
            indirectIntensity = Mathf.Max(0f, indirectIntensity);
            surfaceNormalBias = Mathf.Max(0f, surfaceNormalBias);
            hdrpViewBias = Mathf.Max(0f, hdrpViewBias);
            hdrpAlbedoWeight = Mathf.Clamp01(hdrpAlbedoWeight);
            surfaceVisibilityMaxSteps = Mathf.Clamp(surfaceVisibilityMaxSteps, 4, 128);
            replacementDarkeningStrength = Mathf.Clamp01(replacementDarkeningStrength);
        }
    }
}
