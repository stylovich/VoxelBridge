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
        private static readonly int ProviderModeId = Shader.PropertyToID("_DynamicGI_IndirectProviderMode");
        private static readonly int ScreenSpaceBridgeEnabledId = Shader.PropertyToID("_DynamicGI_ScreenSpaceBridgeEnabled");
        private static readonly int HdrpAlbedoWeightId = Shader.PropertyToID("_DynamicGI_HDRPAlbedoWeight");

        [Header("Indirect provider")]
        [SerializeField] private IndirectLightingProviderMode providerMode = IndirectLightingProviderMode.ExistingPlusDynamic;
        [SerializeField, Min(0f)] private float dynamicGIStrength = 1f;
        [SerializeField, Range(0f, 1f)] private float occlusionStrength = 0.5f;
        [SerializeField, Range(0f, 2f)] private float indirectSaturation = 1f;
        [SerializeField, Min(0f)] private float indirectIntensity = 1f;

        [Header("HDRP stock-material bridge")]
        [Tooltip("Adds Dynamic GI to opaque HDRP camera color before transparents. It does not remove APV; use the material HLSL API for DynamicOnly mode.")]
        [SerializeField] private bool screenSpaceBridgeEnabled = true;
        [Tooltip("0 supports deferred and forward opaque materials uniformly. Values above 0 tint the bridge using HDRP GBuffer0 and are intended for deferred Lit materials.")]
        [SerializeField, Range(0f, 1f)] private float hdrpAlbedoWeight;

        public static DynamicGIShaderGlobals Active { get; private set; }
        public IndirectLightingProviderMode ProviderMode => providerMode;
        public float DynamicGIStrength => dynamicGIStrength;
        public float OcclusionStrength => occlusionStrength;
        public float IndirectSaturation => indirectSaturation;
        public float IndirectIntensity => indirectIntensity;
        public bool ScreenSpaceBridgeEnabled => screenSpaceBridgeEnabled;
        public float HdrpAlbedoWeight => hdrpAlbedoWeight;

        public void Configure(
            IndirectLightingProviderMode mode,
            float strength,
            float ambientOcclusionStrength,
            float saturation,
            float intensity,
            bool enableScreenSpaceBridge,
            float bridgeAlbedoWeight = 0f)
        {
            providerMode = mode;
            dynamicGIStrength = Mathf.Max(0f, strength);
            occlusionStrength = Mathf.Clamp01(ambientOcclusionStrength);
            indirectSaturation = Mathf.Clamp(saturation, 0f, 2f);
            indirectIntensity = Mathf.Max(0f, intensity);
            screenSpaceBridgeEnabled = enableScreenSpaceBridge;
            hdrpAlbedoWeight = Mathf.Clamp01(bridgeAlbedoWeight);
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
            Shader.SetGlobalInt(ProviderModeId, (int)providerMode);
            Shader.SetGlobalInt(ScreenSpaceBridgeEnabledId, screenSpaceBridgeEnabled ? 1 : 0);
            Shader.SetGlobalFloat(HdrpAlbedoWeightId, hdrpAlbedoWeight);
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
            Shader.SetGlobalInt(ProviderModeId, (int)IndirectLightingProviderMode.ExistingOnly);
            Shader.SetGlobalInt(ScreenSpaceBridgeEnabledId, 0);
            Shader.SetGlobalFloat(HdrpAlbedoWeightId, 0f);
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
            hdrpAlbedoWeight = Mathf.Clamp01(hdrpAlbedoWeight);
        }
    }
}
