#ifndef DYNAMIC_GI_RADIANCE_FIELD_INCLUDED
#define DYNAMIC_GI_RADIANCE_FIELD_INCLUDED

Texture3D<float4> _DynamicGI_RadiancePositiveX;
Texture3D<float4> _DynamicGI_RadianceNegativeX;
Texture3D<float4> _DynamicGI_RadiancePositiveY;
Texture3D<float4> _DynamicGI_RadianceNegativeY;
Texture3D<float4> _DynamicGI_RadiancePositiveZ;
Texture3D<float4> _DynamicGI_RadianceNegativeZ;
SamplerState sampler_DynamicGI_RadiancePositiveX;

float3 _DynamicGI_RadianceOrigin;
float3 _DynamicGI_RadianceSize;
float3 _DynamicGI_RadianceResolution;
int _DynamicGI_RadianceAvailable;

float3 SampleDynamicGI(float3 positionWS, float3 normalWS)
{
    float3 relative = positionWS - _DynamicGI_RadianceOrigin;
    if (_DynamicGI_RadianceAvailable == 0 || any(relative < 0.0) || any(relative >= _DynamicGI_RadianceSize))
        return 0.0;

    float3 uvw = saturate(relative / _DynamicGI_RadianceSize);
    float3 normal = normalize(normalWS);
    float3 weights = abs(normal);
    weights /= max(1e-5, weights.x + weights.y + weights.z);

    float3 x = normal.x >= 0.0
        ? _DynamicGI_RadiancePositiveX.SampleLevel(sampler_DynamicGI_RadiancePositiveX, uvw, 0.0).rgb
        : _DynamicGI_RadianceNegativeX.SampleLevel(sampler_DynamicGI_RadiancePositiveX, uvw, 0.0).rgb;
    float3 y = normal.y >= 0.0
        ? _DynamicGI_RadiancePositiveY.SampleLevel(sampler_DynamicGI_RadiancePositiveX, uvw, 0.0).rgb
        : _DynamicGI_RadianceNegativeY.SampleLevel(sampler_DynamicGI_RadiancePositiveX, uvw, 0.0).rgb;
    float3 z = normal.z >= 0.0
        ? _DynamicGI_RadiancePositiveZ.SampleLevel(sampler_DynamicGI_RadiancePositiveX, uvw, 0.0).rgb
        : _DynamicGI_RadianceNegativeZ.SampleLevel(sampler_DynamicGI_RadiancePositiveX, uvw, 0.0).rgb;
    return max(0.0, x * weights.x + y * weights.y + z * weights.z);
}

void SampleDynamicGI_float(float3 PositionWS, float3 NormalWS, out float3 DynamicGI)
{
    DynamicGI = SampleDynamicGI(PositionWS, NormalWS);
}

#endif
