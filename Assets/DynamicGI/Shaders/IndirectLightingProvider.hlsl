#ifndef DYNAMIC_GI_INDIRECT_LIGHTING_PROVIDER_INCLUDED
#define DYNAMIC_GI_INDIRECT_LIGHTING_PROVIDER_INCLUDED

#include "RadianceField.hlsl"
#include "SkyVisibilityField.hlsl"

#define DYNAMIC_GI_PROVIDER_EXISTING_PLUS_DYNAMIC 0
#define DYNAMIC_GI_PROVIDER_DYNAMIC_ONLY 1
#define DYNAMIC_GI_PROVIDER_EXISTING_ONLY 2
#define DYNAMIC_GI_PROVIDER_DISABLED 3

float _DynamicGI_Strength;
float _DynamicGI_OcclusionStrength;
float _DynamicGI_IndirectSaturation;
float _DynamicGI_IndirectIntensity;
int _DynamicGI_IndirectProviderMode;
int _DynamicGI_ScreenSpaceBridgeEnabled;

float3 ApplyDynamicGIControls(float3 value)
{
    value = max(0.0, value);
    float luminance = dot(value, float3(0.2126, 0.7152, 0.0722));
    value = lerp(luminance.xxx, value, saturate(_DynamicGI_IndirectSaturation));
    return max(0.0, value) * max(0.0, _DynamicGI_IndirectIntensity) * max(0.0, _DynamicGI_Strength);
}

float3 SampleControlledDynamicGI(float3 positionWS, float3 normalWS)
{
    return ApplyDynamicGIControls(SampleDynamicGI(positionWS, normalWS));
}

// Accessibility is deliberately separate from radiance composition. Multiplying all
// existing/APV indirect by this scalar would erase valid baked solar bounce. Materials
// with a separable sky/ambient lobe may opt into it explicitly.
float SampleDynamicGIAmbientAccessibility(float3 positionWS)
{
    return lerp(1.0, SampleSkyVisibility(positionWS), saturate(_DynamicGI_OcclusionStrength));
}

float3 SampleIndirectLightingProvider(float3 positionWS, float3 normalWS, float3 existingIndirect)
{
    float3 dynamicIndirect = SampleControlledDynamicGI(positionWS, normalWS);
    if (_DynamicGI_IndirectProviderMode == DYNAMIC_GI_PROVIDER_DYNAMIC_ONLY)
        return dynamicIndirect;
    if (_DynamicGI_IndirectProviderMode == DYNAMIC_GI_PROVIDER_EXISTING_ONLY)
        return max(0.0, existingIndirect);
    if (_DynamicGI_IndirectProviderMode == DYNAMIC_GI_PROVIDER_DISABLED)
        return 0.0;
    return max(0.0, existingIndirect) + dynamicIndirect;
}

// The stock-material HDRP bridge is additive by construction. DynamicOnly still adds
// Dynamic GI, but cannot remove APV already evaluated by HDRP; use the provider above
// inside the material when a true replacement is required.
float3 SampleDynamicGIForAdditiveBridge(float3 positionWS, float3 normalWS)
{
    if (_DynamicGI_ScreenSpaceBridgeEnabled == 0 ||
        _DynamicGI_IndirectProviderMode == DYNAMIC_GI_PROVIDER_EXISTING_ONLY ||
        _DynamicGI_IndirectProviderMode == DYNAMIC_GI_PROVIDER_DISABLED)
        return 0.0;
    return SampleControlledDynamicGI(positionWS, normalWS);
}

// Shader Graph File-mode Custom Function entry points.
void SampleControlledDynamicGI_float(float3 PositionWS, float3 NormalWS, out float3 DynamicGI)
{
    DynamicGI = SampleControlledDynamicGI(PositionWS, NormalWS);
}

void SampleIndirectLightingProvider_float(
    float3 PositionWS,
    float3 NormalWS,
    float3 ExistingIndirect,
    out float3 FinalIndirect)
{
    FinalIndirect = SampleIndirectLightingProvider(PositionWS, NormalWS, ExistingIndirect);
}

void SampleDynamicGIAmbientAccessibility_float(float3 PositionWS, out float Accessibility)
{
    Accessibility = SampleDynamicGIAmbientAccessibility(PositionWS);
}

#endif
