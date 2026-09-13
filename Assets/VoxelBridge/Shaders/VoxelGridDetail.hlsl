#ifndef VOXEL_BRIDGE_GRID_DETAIL_INCLUDED
#define VOXEL_BRIDGE_GRID_DETAIL_INCLUDED

// xy: groove slope; z: roughness mask. Both are attenuated when unresolved.
float3 VoxelGridPattern(float2 q, float2 footprint, float width)
{
    float2 f = frac(q);
    float2 d = 0.5 - abs(f - 0.5);
    float2 effectiveWidth = max(width, footprint * 0.5);
    float2 t = saturate(d / effectiveWidth);
    float2 profile = t * t * (3.0 - 2.0 * t);
    float2 slope = 6.0 * t * (1.0 - t) * sign(0.5 - f);
    slope *= float2(profile.y, profile.x) * (width / effectiveWidth);
    float filtered = 1.0 - smoothstep(0.15, 0.65, max(footprint.x, footprint.y));
    return float3(slope, 1.0 - profile.x * profile.y) * filtered;
}

// Rounded-square height field: a flat recessed joint, a smooth bevel and a flat face.
// Depth/width are fractions of the displayed cell. xy: height gradient, z: joint mask, w: height.
float4 VoxelGridBevelPattern(float2 q, float2 footprint, float jointWidth, float bevelWidth, float depth)
{
    float halfSize = 0.5 - clamp(jointWidth * 0.5, 0.0, 0.24);
    float bevel = clamp(bevelWidth, 0.005, 0.2);
    float radius = min(bevel * 2.0, halfSize);
    float2 centered = frac(q) - 0.5;
    float2 corner = abs(centered) - (halfSize - radius);
    float2 outside = max(corner, 0.0);
    float outsideLength = length(outside);
    float insideDistance = radius - outsideLength - min(max(corner.x, corner.y), 0.0);
    float2 nearestAxis = corner.x >= corner.y ? float2(1, 0) : float2(0, 1);
    float2 inward = -(outsideLength > 0.000001 ? outside / max(outsideLength, 0.000001) : nearestAxis) * sign(centered);
    float effectiveBevel = max(bevel, max(footprint.x, footprint.y) * 0.5);
    float t = saturate(insideDistance / effectiveBevel);
    float face = t * t * (3.0 - 2.0 * t);
    float safeDepth = clamp(depth, 0.0, 0.25);
    float2 gradient = inward * (safeDepth / effectiveBevel) * (6.0 * t * (1.0 - t));
    float filtered = 1.0 - smoothstep(0.15, 0.65, max(footprint.x, footprint.y));
    // No depth means no bevel or joint contribution; color/IDs remain untouched.
    return float4(gradient, (1.0 - face) * step(0.000001, safeDepth), -safeDepth * (1.0 - face)) * filtered;
}

float3 VoxelGridSurfacePattern(float2 q, float2 footprint, float jointWidth, float profile, float bevelWidth, float depth)
{
    if (profile >= 0.5) return VoxelGridBevelPattern(q, footprint, jointWidth, bevelWidth, depth).xyz;
    return VoxelGridPattern(q, footprint, clamp(jointWidth * 0.5, 0.005, 0.24));
}

float VoxelGridDistanceLevel(float cameraDistance, float startDistance, float transitionDistance, float maxLevels)
{
    return clamp((cameraDistance - max(startDistance, 0.0)) / max(transitionDistance, 0.01),
        0.0, floor(clamp(maxLevels, 0.0, 8.0)));
}

// Family-local requires a shared renderer frame (no static batching). UV0/UV3 remain IDs.
void VoxelGridDetail_float(float3 Position, float3 Normal, float3 Tangent, float3 Bitangent,
    float Enabled, float CellSize, float JointWidth, float NormalStrength,
    float RoughnessStrength, float FadeStart, float FadeEnd, float BaseSmoothness,
    float Multiscale, float TargetPixels, float MaxScaleLevels,
    float AnchorMode, float DistanceStart, float DistanceStep,
    float Profile, float BevelWidth, float JointDepth,
    out float3 DetailNormalTS, out float DetailSmoothness)
{
    DetailNormalTS = float3(0, 0, 1);
    DetailSmoothness = BaseSmoothness;
#if !defined(SHADERGRAPH_PREVIEW) && !defined(SHADER_STAGE_RAY_TRACING)
    float3 n = normalize(Normal);
    float3 frameNormal = n;
    float3 framePosition = GetAbsolutePositionWS(Position);
    float3x3 modelToWorld = (float3x3)GetObjectToWorldMatrix();
    if (AnchorMode >= 0.5)
    {
        framePosition = TransformWorldToObject(Position);
        frameNormal = normalize(mul(n, modelToWorld));
    }
    Position = GetAbsolutePositionWS(Position);
    float3 a = abs(frameNormal);
    float3 u = a.x >= a.y && a.x >= a.z ? float3(0, 1, 0) : float3(1, 0, 0);
    float3 v = a.z > a.x && a.z > a.y ? float3(0, 1, 0) : float3(0, 0, 1);
    float2 q = float2(dot(framePosition, u), dot(framePosition, v));
    if (AnchorMode >= 0.5)
    {
        // Preserve metres under non-uniform (non-sheared) instance scaling.
        float3 worldU = mul(modelToWorld, u), worldV = mul(modelToWorld, v);
        float2 axisScale = max(float2(length(worldU), length(worldV)), 0.000001);
        q *= axisScale;
        u = worldU / axisScale.x; v = worldV / axisScale.y;
    }
    q /= max(CellSize, 0.001);
    // Evaluate derivatives before branching. Fade unresolved cells instead of aliasing.
    float2 footprint = max(fwidth(q), 0.0001);
    float weight = saturate(Enabled);
    if (weight <= 0.0001) return;
    float3 pattern;
    if (Multiscale >= 0.5)
    {
        // Crossfade two fixed, nested grids instead of stretching the grid with the camera.
        // Distance selection ignores face angle; derivative filtering below still prevents aliasing.
        float maxLevel = floor(clamp(MaxScaleLevels, 0.0, 8.0));
        float level;
        if (Multiscale >= 1.5)
            level = VoxelGridDistanceLevel(distance(Position, _WorldSpaceCameraPos), DistanceStart, DistanceStep, maxLevel);
        else
            level = clamp(log2(max(max(footprint.x, footprint.y) * clamp(TargetPixels, 4.0, 64.0), 1.0)), 0.0, maxLevel);
        float lower = floor(level);
        float upper = min(lower + 1.0, maxLevel);
        float lowerScale = exp2(lower), upperScale = exp2(upper);
        float blend = smoothstep(0.0, 1.0, frac(level));
        pattern = lerp(VoxelGridSurfacePattern(q / lowerScale, footprint / lowerScale, JointWidth, Profile, BevelWidth, JointDepth),
            VoxelGridSurfacePattern(q / upperScale, footprint / upperScale, JointWidth, Profile, BevelWidth, JointDepth), blend);
        // At the size cap, filtering still fades detail that can no longer be resolved.
    }
    else
    {
        float cameraDistance = distance(Position, _WorldSpaceCameraPos);
        weight *= 1.0 - smoothstep(max(0, FadeStart), max(FadeStart + 0.01, FadeEnd), cameraDistance);
        pattern = VoxelGridSurfacePattern(q, footprint, JointWidth, Profile, BevelWidth, JointDepth);
    }
    float3 gradient = u * pattern.x + v * pattern.y;
    gradient -= n * dot(gradient, n);
    float3 detailN = normalize(n - gradient * saturate(NormalStrength) * weight);
    DetailNormalTS = normalize(float3(dot(detailN, normalize(Tangent)),
        dot(detailN, normalize(Bitangent)), dot(detailN, n)));
    DetailSmoothness = saturate(BaseSmoothness - pattern.z * saturate(RoughnessStrength) * weight);
#endif
}

// Preserve the distance-only signature and its original line profile.
void VoxelGridDetail_float(float3 Position, float3 Normal, float3 Tangent, float3 Bitangent,
    float Enabled, float CellSize, float JointWidth, float NormalStrength,
    float RoughnessStrength, float FadeStart, float FadeEnd, float BaseSmoothness,
    float Multiscale, float TargetPixels, float MaxScaleLevels,
    float AnchorMode, float DistanceStart, float DistanceStep,
    out float3 DetailNormalTS, out float DetailSmoothness)
{
    VoxelGridDetail_float(Position, Normal, Tangent, Bitangent, Enabled, CellSize, JointWidth,
        NormalStrength, RoughnessStrength, FadeStart, FadeEnd, BaseSmoothness,
        Multiscale, TargetPixels, MaxScaleLevels, AnchorMode, DistanceStart, DistanceStep,
        0.0, 0.06, 0.025, DetailNormalTS, DetailSmoothness);
}

// Retain the original signature for the standalone prototype and external graphs.
void VoxelGridDetail_float(float3 Position, float3 Normal, float3 Tangent, float3 Bitangent,
    float Enabled, float CellSize, float JointWidth, float NormalStrength,
    float RoughnessStrength, float FadeStart, float FadeEnd, float BaseSmoothness,
    float Multiscale, float TargetPixels, float MaxScaleLevels, float AnchorMode,
    out float3 DetailNormalTS, out float DetailSmoothness)
{
    VoxelGridDetail_float(Position, Normal, Tangent, Bitangent, Enabled, CellSize, JointWidth,
        NormalStrength, RoughnessStrength, FadeStart, FadeEnd, BaseSmoothness,
        Multiscale, TargetPixels, MaxScaleLevels, AnchorMode, 12.0, 20.0,
        DetailNormalTS, DetailSmoothness);
}
#endif
