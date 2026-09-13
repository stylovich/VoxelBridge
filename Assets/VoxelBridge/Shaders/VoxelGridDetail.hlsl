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

// Static, axis-aligned prototype. UV0/UV3 remain semantic IDs. Geometric LOD is independent.
void VoxelGridDetail_float(float3 Position, float3 Normal, float3 Tangent, float3 Bitangent,
    float Enabled, float CellSize, float JointWidth, float NormalStrength,
    float RoughnessStrength, float FadeStart, float FadeEnd, float BaseSmoothness,
    float Multiscale, float TargetPixels, float MaxScaleLevels,
    out float3 DetailNormalTS, out float DetailSmoothness)
{
    DetailNormalTS = float3(0, 0, 1);
    DetailSmoothness = BaseSmoothness;
#if !defined(SHADERGRAPH_PREVIEW) && !defined(SHADER_STAGE_RAY_TRACING)
    Position = GetAbsolutePositionWS(Position);
    float3 n = normalize(Normal);
    float3 a = abs(n);
    float3 u = a.x >= a.y && a.x >= a.z ? float3(0, 1, 0) : float3(1, 0, 0);
    float3 v = a.z > a.x && a.z > a.y ? float3(0, 1, 0) : float3(0, 0, 1);
    float2 q = float2(dot(Position, u), dot(Position, v)) / max(CellSize, 0.001);
    // Evaluate derivatives before branching. Fade unresolved cells instead of aliasing.
    float2 footprint = max(fwidth(q), 0.0001);
    float weight = saturate(Enabled);
    if (weight <= 0.0001) return;
    float width = clamp(JointWidth * 0.5, 0.005, 0.24);
    float3 pattern;
    if (Multiscale >= 0.5)
    {
        // Crossfade two fixed, nested grids instead of stretching the grid with the camera.
        // Logarithmic selection responds to resolution/FOV/obliquity, not the mesh's LOD index.
        float maxLevel = floor(clamp(MaxScaleLevels, 0.0, 8.0));
        float level = clamp(log2(max(max(footprint.x, footprint.y) * clamp(TargetPixels, 4.0, 64.0), 1.0)), 0.0, maxLevel);
        float lower = floor(level);
        float upper = min(lower + 1.0, maxLevel);
        float lowerScale = exp2(lower), upperScale = exp2(upper);
        float blend = smoothstep(0.0, 1.0, frac(level));
        pattern = lerp(VoxelGridPattern(q / lowerScale, footprint / lowerScale, width),
            VoxelGridPattern(q / upperScale, footprint / upperScale, width), blend);
        // At the size cap, filtering still fades detail that can no longer be resolved.
    }
    else
    {
        float cameraDistance = distance(Position, _WorldSpaceCameraPos);
        weight *= 1.0 - smoothstep(max(0, FadeStart), max(FadeStart + 0.01, FadeEnd), cameraDistance);
        pattern = VoxelGridPattern(q, footprint, width);
    }
    float3 gradient = u * pattern.x + v * pattern.y;
    gradient -= n * dot(gradient, n);
    float3 detailN = normalize(n - gradient * saturate(NormalStrength) * weight);
    DetailNormalTS = normalize(float3(dot(detailN, normalize(Tangent)),
        dot(detailN, normalize(Bitangent)), dot(detailN, n)));
    DetailSmoothness = saturate(BaseSmoothness - pattern.z * saturate(RoughnessStrength) * weight);
#endif
}
#endif
