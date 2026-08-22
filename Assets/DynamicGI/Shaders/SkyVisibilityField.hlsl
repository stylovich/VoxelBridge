#ifndef DYNAMIC_GI_SKY_VISIBILITY_FIELD_INCLUDED
#define DYNAMIC_GI_SKY_VISIBILITY_FIELD_INCLUDED

Texture3D<float> _DynamicGI_SkyVisibilityTexture;
SamplerState sampler_DynamicGI_SkyVisibilityTexture;

float3 _DynamicGI_SkyVisibilityOrigin;
float3 _DynamicGI_SkyVisibilitySize;
float3 _DynamicGI_SkyVisibilityResolution;
int _DynamicGI_SkyVisibilityAvailable;

float SampleSkyVisibility(float3 positionWS)
{
    float3 relative = positionWS - _DynamicGI_SkyVisibilityOrigin;
    if (_DynamicGI_SkyVisibilityAvailable == 0 || any(relative < 0.0) || any(relative >= _DynamicGI_SkyVisibilitySize))
        return 1.0;

    float3 uvw = saturate(relative / _DynamicGI_SkyVisibilitySize);
    return saturate(_DynamicGI_SkyVisibilityTexture.SampleLevel(
        sampler_DynamicGI_SkyVisibilityTexture,
        uvw,
        0.0));
}

// Shader Graph Custom Function entry point.
void SampleSkyVisibility_float(float3 PositionWS, out float SkyVisibility)
{
    SkyVisibility = SampleSkyVisibility(PositionWS);
}

#endif
