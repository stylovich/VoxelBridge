#ifndef VOXEL_BRIDGE_GRID_DETAIL_INCLUDED
#define VOXEL_BRIDGE_GRID_DETAIL_INCLUDED

// Static, axis-aligned architecture prototype. UV0/UV3 remain semantic IDs.
void VoxelGridDetail_float(float3 Position, float3 Normal, float3 Tangent, float3 Bitangent,
    float Enabled, float CellSize, float JointWidth, float NormalStrength,
    float RoughnessStrength, float FadeStart, float FadeEnd, float BaseSmoothness,
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
    float resolutionFade = 1.0 - smoothstep(0.15, 0.65, max(footprint.x, footprint.y));
    float cameraDistance = distance(Position, _WorldSpaceCameraPos);
    float distanceFade = 1.0 - smoothstep(max(0, FadeStart), max(FadeStart + 0.01, FadeEnd), cameraDistance);
    float weight = saturate(Enabled) * resolutionFade * distanceFade;
    if (weight <= 0.0001) return;

    float2 f = frac(q);
    float2 d = 0.5 - abs(f - 0.5);
    float width = clamp(JointWidth * 0.5, 0.005, 0.24);
    float2 effectiveWidth = max(width, footprint * 0.5);
    float2 t = saturate(d / effectiveWidth);
    float2 profile = t * t * (3.0 - 2.0 * t);
    float joint = 1.0 - profile.x * profile.y;
    float2 slope = 6.0 * t * (1.0 - t) * sign(0.5 - f);
    slope *= float2(profile.y, profile.x) * (width / effectiveWidth);
    float3 gradient = u * slope.x + v * slope.y;
    gradient -= n * dot(gradient, n);
    float3 detailN = normalize(n - gradient * saturate(NormalStrength) * weight);
    DetailNormalTS = normalize(float3(dot(detailN, normalize(Tangent)),
        dot(detailN, normalize(Bitangent)), dot(detailN, n)));
    DetailSmoothness = saturate(BaseSmoothness - joint * saturate(RoughnessStrength) * weight);
#endif
}
#endif
