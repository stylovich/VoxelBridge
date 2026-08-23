#ifndef DYNAMIC_GI_RADIANCE_FIELD_INCLUDED
#define DYNAMIC_GI_RADIANCE_FIELD_INCLUDED

#include "GeometryField.hlsl"

// Phase 4 local field.
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

// Phase 5 clipmap. A maximum of four cascades keeps bindings explicit and portable.
#define DYNAMIC_GI_DECLARE_CASCADE_TEXTURES(I) \
Texture3D<float4> _DynamicGI_RadianceCascade##I##PositiveX; \
Texture3D<float4> _DynamicGI_RadianceCascade##I##NegativeX; \
Texture3D<float4> _DynamicGI_RadianceCascade##I##PositiveY; \
Texture3D<float4> _DynamicGI_RadianceCascade##I##NegativeY; \
Texture3D<float4> _DynamicGI_RadianceCascade##I##PositiveZ; \
Texture3D<float4> _DynamicGI_RadianceCascade##I##NegativeZ; \
float3 _DynamicGI_RadianceCascade##I##Origin; \
float3 _DynamicGI_RadianceCascade##I##Size; \
float3 _DynamicGI_RadianceCascade##I##Resolution; \
float3 _DynamicGI_RadianceCascade##I##RingOffset; \
int _DynamicGI_RadianceCascade##I##Available;

DYNAMIC_GI_DECLARE_CASCADE_TEXTURES(0)
DYNAMIC_GI_DECLARE_CASCADE_TEXTURES(1)
DYNAMIC_GI_DECLARE_CASCADE_TEXTURES(2)
DYNAMIC_GI_DECLARE_CASCADE_TEXTURES(3)

int _DynamicGI_RadianceClipmapAvailable;
int _DynamicGI_RadianceCascadeCount;
float _DynamicGI_RadianceCascadeBlendStart;
int _DynamicGI_GeometryAwareSurfaceSampling;
int _DynamicGI_SurfaceVisibilityMaxSteps;
float _DynamicGI_SurfaceVisibilityWeightFloor;

void DynamicGIGetCascadeLayout(int cascadeIndex, out float3 origin, out float3 size, out int3 resolution, out int3 ringOffset)
{
    if (cascadeIndex == 0)
    {
        origin = _DynamicGI_RadianceCascade0Origin; size = _DynamicGI_RadianceCascade0Size;
        resolution = (int3)_DynamicGI_RadianceCascade0Resolution; ringOffset = (int3)_DynamicGI_RadianceCascade0RingOffset;
    }
    else if (cascadeIndex == 1)
    {
        origin = _DynamicGI_RadianceCascade1Origin; size = _DynamicGI_RadianceCascade1Size;
        resolution = (int3)_DynamicGI_RadianceCascade1Resolution; ringOffset = (int3)_DynamicGI_RadianceCascade1RingOffset;
    }
    else if (cascadeIndex == 2)
    {
        origin = _DynamicGI_RadianceCascade2Origin; size = _DynamicGI_RadianceCascade2Size;
        resolution = (int3)_DynamicGI_RadianceCascade2Resolution; ringOffset = (int3)_DynamicGI_RadianceCascade2RingOffset;
    }
    else
    {
        origin = _DynamicGI_RadianceCascade3Origin; size = _DynamicGI_RadianceCascade3Size;
        resolution = (int3)_DynamicGI_RadianceCascade3Resolution; ringOffset = (int3)_DynamicGI_RadianceCascade3RingOffset;
    }
}

float4 DynamicGILoadCascadeDirection(int cascadeIndex, int directionIndex, int3 physicalCoordinate)
{
    float4 value = 0.0;
    if (cascadeIndex == 0)
    {
        if (directionIndex == 0) value = _DynamicGI_RadianceCascade0PositiveX.Load(int4(physicalCoordinate, 0));
        else if (directionIndex == 1) value = _DynamicGI_RadianceCascade0NegativeX.Load(int4(physicalCoordinate, 0));
        else if (directionIndex == 2) value = _DynamicGI_RadianceCascade0PositiveY.Load(int4(physicalCoordinate, 0));
        else if (directionIndex == 3) value = _DynamicGI_RadianceCascade0NegativeY.Load(int4(physicalCoordinate, 0));
        else if (directionIndex == 4) value = _DynamicGI_RadianceCascade0PositiveZ.Load(int4(physicalCoordinate, 0));
        else value = _DynamicGI_RadianceCascade0NegativeZ.Load(int4(physicalCoordinate, 0));
    }
    else if (cascadeIndex == 1)
    {
        if (directionIndex == 0) value = _DynamicGI_RadianceCascade1PositiveX.Load(int4(physicalCoordinate, 0));
        else if (directionIndex == 1) value = _DynamicGI_RadianceCascade1NegativeX.Load(int4(physicalCoordinate, 0));
        else if (directionIndex == 2) value = _DynamicGI_RadianceCascade1PositiveY.Load(int4(physicalCoordinate, 0));
        else if (directionIndex == 3) value = _DynamicGI_RadianceCascade1NegativeY.Load(int4(physicalCoordinate, 0));
        else if (directionIndex == 4) value = _DynamicGI_RadianceCascade1PositiveZ.Load(int4(physicalCoordinate, 0));
        else value = _DynamicGI_RadianceCascade1NegativeZ.Load(int4(physicalCoordinate, 0));
    }
    else if (cascadeIndex == 2)
    {
        if (directionIndex == 0) value = _DynamicGI_RadianceCascade2PositiveX.Load(int4(physicalCoordinate, 0));
        else if (directionIndex == 1) value = _DynamicGI_RadianceCascade2NegativeX.Load(int4(physicalCoordinate, 0));
        else if (directionIndex == 2) value = _DynamicGI_RadianceCascade2PositiveY.Load(int4(physicalCoordinate, 0));
        else if (directionIndex == 3) value = _DynamicGI_RadianceCascade2NegativeY.Load(int4(physicalCoordinate, 0));
        else if (directionIndex == 4) value = _DynamicGI_RadianceCascade2PositiveZ.Load(int4(physicalCoordinate, 0));
        else value = _DynamicGI_RadianceCascade2NegativeZ.Load(int4(physicalCoordinate, 0));
    }
    else
    {
        if (directionIndex == 0) value = _DynamicGI_RadianceCascade3PositiveX.Load(int4(physicalCoordinate, 0));
        else if (directionIndex == 1) value = _DynamicGI_RadianceCascade3NegativeX.Load(int4(physicalCoordinate, 0));
        else if (directionIndex == 2) value = _DynamicGI_RadianceCascade3PositiveY.Load(int4(physicalCoordinate, 0));
        else if (directionIndex == 3) value = _DynamicGI_RadianceCascade3NegativeY.Load(int4(physicalCoordinate, 0));
        else if (directionIndex == 4) value = _DynamicGI_RadianceCascade3PositiveZ.Load(int4(physicalCoordinate, 0));
        else value = _DynamicGI_RadianceCascade3NegativeZ.Load(int4(physicalCoordinate, 0));
    }
    return value;
}

float4 DynamicGILoadCascadeIrradianceAndValidity(int cascadeIndex, int3 logicalCoordinate, int3 resolution, int3 ringOffset, float3 normalWS)
{
    int3 physical = (logicalCoordinate + ringOffset) & (resolution - 1);
    float3 normal = normalize(normalWS);
    float3 weights = abs(normal);
    weights /= max(1e-5, weights.x + weights.y + weights.z);
    int directionX = normal.x >= 0.0 ? 0 : 1;
    int directionY = normal.y >= 0.0 ? 2 : 3;
    int directionZ = normal.z >= 0.0 ? 4 : 5;
    float4 x = DynamicGILoadCascadeDirection(cascadeIndex, directionX, physical);
    float4 y = DynamicGILoadCascadeDirection(cascadeIndex, directionY, physical);
    float4 z = DynamicGILoadCascadeDirection(cascadeIndex, directionZ, physical);
    return float4(
        x.rgb * weights.x + y.rgb * weights.y + z.rgb * weights.z,
        x.a * weights.x + y.a * weights.y + z.a * weights.z);
}

float3 DynamicGILoadCascadeIrradiance(int cascadeIndex, int3 logicalCoordinate, int3 resolution, int3 ringOffset, float3 normalWS)
{
    return DynamicGILoadCascadeIrradianceAndValidity(
        cascadeIndex,
        logicalCoordinate,
        resolution,
        ringOffset,
        normalWS).rgb;
}

bool DynamicGICascadeContains(int cascadeIndex, float3 positionWS)
{
    float3 origin, size;
    int3 resolution, ringOffset;
    DynamicGIGetCascadeLayout(cascadeIndex, origin, size, resolution, ringOffset);
    float3 relative = positionWS - origin;
    return all(relative >= 0.0) && all(relative < size);
}

float DynamicGICascadeEdgeFactor(int cascadeIndex, float3 positionWS)
{
    float3 origin, size;
    int3 resolution, ringOffset;
    DynamicGIGetCascadeLayout(cascadeIndex, origin, size, resolution, ringOffset);
    float3 normalizedDistance = abs(positionWS - (origin + size * 0.5)) / max(size * 0.5, 1e-5);
    return max(normalizedDistance.x, max(normalizedDistance.y, normalizedDistance.z));
}

float3 DynamicGISampleRadianceCascade(int cascadeIndex, float3 positionWS, float3 normalWS)
{
    float3 origin, size;
    int3 resolution, ringOffset;
    DynamicGIGetCascadeLayout(cascadeIndex, origin, size, resolution, ringOffset);
    float3 gridPosition = (positionWS - origin) / (size / (float3)resolution) - 0.5;
    int3 baseCoordinate = (int3)floor(gridPosition);
    float3 fraction = frac(gridPosition);
    int3 c000 = clamp(baseCoordinate, 0, resolution - 1);
    int3 c111 = clamp(baseCoordinate + 1, 0, resolution - 1);

    float3 v000 = DynamicGILoadCascadeIrradiance(cascadeIndex, int3(c000.x, c000.y, c000.z), resolution, ringOffset, normalWS);
    float3 v100 = DynamicGILoadCascadeIrradiance(cascadeIndex, int3(c111.x, c000.y, c000.z), resolution, ringOffset, normalWS);
    float3 v010 = DynamicGILoadCascadeIrradiance(cascadeIndex, int3(c000.x, c111.y, c000.z), resolution, ringOffset, normalWS);
    float3 v110 = DynamicGILoadCascadeIrradiance(cascadeIndex, int3(c111.x, c111.y, c000.z), resolution, ringOffset, normalWS);
    float3 v001 = DynamicGILoadCascadeIrradiance(cascadeIndex, int3(c000.x, c000.y, c111.z), resolution, ringOffset, normalWS);
    float3 v101 = DynamicGILoadCascadeIrradiance(cascadeIndex, int3(c111.x, c000.y, c111.z), resolution, ringOffset, normalWS);
    float3 v011 = DynamicGILoadCascadeIrradiance(cascadeIndex, int3(c000.x, c111.y, c111.z), resolution, ringOffset, normalWS);
    float3 v111 = DynamicGILoadCascadeIrradiance(cascadeIndex, int3(c111.x, c111.y, c111.z), resolution, ringOffset, normalWS);
    float3 z0 = lerp(lerp(v000, v100, fraction.x), lerp(v010, v110, fraction.x), fraction.y);
    float3 z1 = lerp(lerp(v001, v101, fraction.x), lerp(v011, v111, fraction.x), fraction.y);
    return max(0.0, lerp(z0, z1, fraction.z));
}

float DynamicGIVisibleProbeWeight(
    float3 samplePositionWS,
    float3 probePositionWS,
    float interpolationWeight,
    float probeValidity)
{
    float weight = interpolationWeight * saturate(probeValidity);
    if (weight <= 1e-6 || _DynamicGI_GeometryFieldAvailable == 0)
        return weight;

    float3 toProbe = probePositionWS - samplePositionWS;
    float distanceToProbe = length(toProbe);
    if (distanceToProbe <= max(1e-4, _DynamicGI_GeometryVoxelSize * 0.1))
        return weight;

    float endpointBias = min(_DynamicGI_GeometryVoxelSize * 0.35, distanceToProbe * 0.2);
    bool blocked = DynamicGITraceGeometryDDA(
        samplePositionWS,
        toProbe / distanceToProbe,
        max(0.0, distanceToProbe - endpointBias),
        max(1, _DynamicGI_SurfaceVisibilityMaxSteps));
    return blocked ? 0.0 : weight;
}

void DynamicGIAccumulateVisibleCascadeProbe(
    int cascadeIndex,
    int3 logicalCoordinate,
    int3 resolution,
    int3 ringOffset,
    float3 cascadeOrigin,
    float3 cellSize,
    float3 samplePositionWS,
    float3 normalWS,
    float interpolationWeight,
    inout float3 weightedRadiance,
    inout float accumulatedWeight,
    inout float3 unresolvedRadiance,
    inout float unresolvedWeight)
{
    float4 value = DynamicGILoadCascadeIrradianceAndValidity(
        cascadeIndex,
        logicalCoordinate,
        resolution,
        ringOffset,
        normalWS);
    float candidateWeight = interpolationWeight * saturate(value.a);
    unresolvedRadiance += value.rgb * candidateWeight;
    unresolvedWeight += candidateWeight;
    float3 probePositionWS = cascadeOrigin + ((float3)logicalCoordinate + 0.5) * cellSize;
    float visibleWeight = DynamicGIVisibleProbeWeight(
        samplePositionWS,
        probePositionWS,
        interpolationWeight,
        value.a);
    weightedRadiance += value.rgb * visibleWeight;
    accumulatedWeight += visibleWeight;
}

// Correctness-first surface lookup. Unlike raw trilinear filtering, each candidate
// probe must have line of sight to the biased surface sample through the Geometry
// Field. This prevents exterior probes from bleeding through walls and T-junctions.
// It costs up to eight short DDA traversals per sampled cascade, so callers can turn
// it off globally when using a cheaper material integration.
float3 DynamicGISampleRadianceCascadeAtSurface(int cascadeIndex, float3 positionWS, float3 normalWS)
{
    float3 origin, size;
    int3 resolution, ringOffset;
    DynamicGIGetCascadeLayout(cascadeIndex, origin, size, resolution, ringOffset);
    float3 cellSize = size / (float3)resolution;
    float3 gridPosition = (positionWS - origin) / cellSize - 0.5;
    int3 baseCoordinate = (int3)floor(gridPosition);
    float3 fraction = frac(gridPosition);
    int3 c0 = clamp(baseCoordinate, 0, resolution - 1);
    int3 c1 = clamp(baseCoordinate + 1, 0, resolution - 1);
    float3 inverseFraction = 1.0 - fraction;

    float3 weightedRadiance = 0.0;
    float accumulatedWeight = 0.0;
    float3 unresolvedRadiance = 0.0;
    float unresolvedWeight = 0.0;
    [loop]
    for (int cornerIndex = 0; cornerIndex < 8; cornerIndex++)
    {
        int3 selectUpper = int3(cornerIndex & 1, (cornerIndex >> 1) & 1, (cornerIndex >> 2) & 1);
        int3 coordinate = int3(
            selectUpper.x != 0 ? c1.x : c0.x,
            selectUpper.y != 0 ? c1.y : c0.y,
            selectUpper.z != 0 ? c1.z : c0.z);
        float3 cornerWeight = float3(
            selectUpper.x != 0 ? fraction.x : inverseFraction.x,
            selectUpper.y != 0 ? fraction.y : inverseFraction.y,
            selectUpper.z != 0 ? fraction.z : inverseFraction.z);
        DynamicGIAccumulateVisibleCascadeProbe(
            cascadeIndex,
            coordinate,
            resolution,
            ringOffset,
            origin,
            cellSize,
            positionWS,
            normalWS,
            cornerWeight.x * cornerWeight.y * cornerWeight.z,
            weightedRadiance,
            accumulatedWeight,
            unresolvedRadiance,
            unresolvedWeight);
    }
    // Do not promote a tiny surviving interpolation corner to full energy. That
    // produces bright plateaus followed by black seams whenever binary DDA visibility
    // changes at a wall. A configurable denominator floor keeps fully visible samples
    // unchanged while turning low-confidence visibility into conservative darkening.
    float normalizationWeight = max(accumulatedWeight, saturate(_DynamicGI_SurfaceVisibilityWeightFloor));
    if (accumulatedWeight > 1e-5)
        return max(0.0, weightedRadiance / max(normalizationWeight, 1e-5));

    // At a perpendicular T-junction the normal-biased sample can still lie in the
    // adjacent wall's surface voxel. DDA then has no unambiguous visible corner. A
    // conservative, validity-weighted trilinear fallback avoids a view-dependent black
    // seam without changing the normal path used by ordinary wall/floor samples.
    float unresolvedNormalization = max(unresolvedWeight, saturate(_DynamicGI_SurfaceVisibilityWeightFloor));
    const float unresolvedJunctionConfidence = 0.5;
    return unresolvedWeight > 1e-5
        ? max(0.0, unresolvedRadiance / max(unresolvedNormalization, 1e-5)) * unresolvedJunctionConfidence
        : 0.0;
}

float3 DynamicGISampleClipmap(float3 positionWS, float3 normalWS)
{
    float3 result = 0.0;
    [loop]
    for (int cascadeIndex = 0; cascadeIndex < 4; cascadeIndex++)
    {
        if (cascadeIndex >= _DynamicGI_RadianceCascadeCount || !DynamicGICascadeContains(cascadeIndex, positionWS))
            continue;

        float3 fine = DynamicGISampleRadianceCascade(cascadeIndex, positionWS, normalWS);
        int coarseIndex = cascadeIndex + 1;
        if (coarseIndex >= _DynamicGI_RadianceCascadeCount || !DynamicGICascadeContains(coarseIndex, positionWS))
        {
            result = fine;
            break;
        }
        float edge = DynamicGICascadeEdgeFactor(cascadeIndex, positionWS);
        float blend = saturate((edge - _DynamicGI_RadianceCascadeBlendStart) /
                               max(1e-4, 1.0 - _DynamicGI_RadianceCascadeBlendStart));
        if (blend <= 0.0)
        {
            result = fine;
            break;
        }
        result = lerp(fine, DynamicGISampleRadianceCascade(coarseIndex, positionWS, normalWS), blend);
        break;
    }
    return result;
}

float3 DynamicGISampleClipmapAtSurface(float3 positionWS, float3 normalWS)
{
    [loop]
    for (int cascadeIndex = 0; cascadeIndex < 4; cascadeIndex++)
    {
        if (cascadeIndex >= _DynamicGI_RadianceCascadeCount || !DynamicGICascadeContains(cascadeIndex, positionWS))
            continue;

        float3 fine = DynamicGISampleRadianceCascadeAtSurface(cascadeIndex, positionWS, normalWS);
        int coarseIndex = cascadeIndex + 1;
        if (coarseIndex >= _DynamicGI_RadianceCascadeCount || !DynamicGICascadeContains(coarseIndex, positionWS))
            return fine;
        float edge = DynamicGICascadeEdgeFactor(cascadeIndex, positionWS);
        float blend = saturate((edge - _DynamicGI_RadianceCascadeBlendStart) /
                               max(1e-4, 1.0 - _DynamicGI_RadianceCascadeBlendStart));
        if (blend <= 0.0)
            return fine;
        return lerp(
            fine,
            DynamicGISampleRadianceCascadeAtSurface(coarseIndex, positionWS, normalWS),
            blend);
    }
    return 0.0;
}

float3 DynamicGISampleLocalRadiance(float3 positionWS, float3 normalWS)
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

float3 SampleDynamicGI(float3 positionWS, float3 normalWS)
{
    return _DynamicGI_RadianceClipmapAvailable != 0
        ? DynamicGISampleClipmap(positionWS, normalWS)
        : DynamicGISampleLocalRadiance(positionWS, normalWS);
}

// Surface materials must not interpolate probes from both sides of a thin shell.
// The caller controls the world-space bias because the appropriate distance depends
// on the near-cascade spacing and the source project's geometry scale.
float3 SampleDynamicGIAtSurface(float3 positionWS, float3 normalWS, float normalBias)
{
    float3 normal = normalWS * rsqrt(max(dot(normalWS, normalWS), 1e-8));
    float3 samplePositionWS = positionWS + normal * max(0.0, normalBias);
    float3 result = 0.0;
    if (_DynamicGI_RadianceClipmapAvailable != 0 && _DynamicGI_GeometryAwareSurfaceSampling != 0)
        result = DynamicGISampleClipmapAtSurface(samplePositionWS, normal);
    else
        result = SampleDynamicGI(samplePositionWS, normal);
    return max(0.0, result);
}

void SampleDynamicGI_float(float3 PositionWS, float3 NormalWS, out float3 DynamicGI)
{
    DynamicGI = SampleDynamicGI(PositionWS, NormalWS);
}


void SampleDynamicGIAtSurface_float(
    float3 PositionWS,
    float3 NormalWS,
    float NormalBias,
    out float3 DynamicGI)
{
    DynamicGI = SampleDynamicGIAtSurface(PositionWS, NormalWS, NormalBias);
}

#endif
