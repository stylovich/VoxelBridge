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

float VoxelGridCellRandom(float3 cell)
{
    uint3 p = asuint((int3)cell);
    uint h = p.x * 0x8da6b343u ^ p.y * 0xd8163841u ^ p.z * 0xcb1ab31fu;
    h ^= h >> 16; h *= 0x7feb352du; h ^= h >> 15; h *= 0x846ca68bu; h ^= h >> 16;
    return (h & 0x00ffffffu) / 16777215.0;
}

float4 VoxelGridVariedBevel(float2 q, float2 footprint, float jointWidth, float bevelWidth,
    float depth, float variation, float3 planeCell, float3 cellU, float3 cellV)
{
    depth = clamp(depth, 0.0, 0.25);
    variation = min(clamp(variation, 0.0, 0.25), 0.25 - depth);
    if (variation <= 0) return VoxelGridBevelPattern(q, footprint, jointWidth, bevelWidth, depth);
    float sink = variation * VoxelGridCellRandom(planeCell + cellU * floor(q.x) + cellV * floor(q.y));
    // A common joint floor avoids a discontinuity when adjacent cells have different heights.
    float4 result = VoxelGridBevelPattern(q, footprint, jointWidth, bevelWidth, depth + variation - sink);
    result.w -= sink * (1.0 - smoothstep(0.15, 0.65, max(footprint.x, footprint.y)));
    return result;
}

float3 VoxelGridSurfacePattern(float2 q, float2 footprint, float jointWidth, float profile, float bevelWidth, float depth,
    float variation, float3 planeCell, float3 cellU, float3 cellV)
{
    if (profile >= 0.5) return VoxelGridVariedBevel(q, footprint, jointWidth, bevelWidth, depth, variation, planeCell, cellU, cellV).xyz;
    return VoxelGridPattern(q, footprint, clamp(jointWidth * 0.5, 0.005, 0.24));
}

float VoxelGridDistanceLevel(float cameraDistance, float startDistance, float transitionDistance, float maxLevels)
{
    return clamp((cameraDistance - max(startDistance, 0.0)) / max(transitionDistance, 0.01),
        0.0, floor(clamp(maxLevels, 0.0, 8.0)));
}

// Intersect a bounded ray with the same filtered procedural bevel height field.
// xy: hit coordinates, z: normalized hit depth. No textures, clipping or depth-buffer writes.
float3 VoxelGridParallax(float2 q, float2 footprint, float jointWidth, float bevelWidth,
    float depthInCells, float3 viewInGrid, float jointDepth, float variation,
    float3 planeCell, float3 cellU, float3 cellV)
{
    if (depthInCells <= 0.000001 || viewInGrid.z <= 0.12) return float3(q, 0);
    float2 ray = -viewInGrid.xy / max(viewInGrid.z, 0.12) * depthInCells;
    float rayLength = length(ray);
    ray *= min(1.0, 0.2 / max(rayLength, 0.000001));
    // Using the existing height output keeps the POM shape identical to the normals.
    float total = max(min(0.25, max(jointDepth, 0.0) + max(variation, 0.0)), 0.000001);
    if (-VoxelGridVariedBevel(q, footprint, jointWidth, bevelWidth, jointDepth, variation, planeCell, cellU, cellV).w / total <= 0.000001)
        return float3(q, 0);
    float lo = 0, hi = 1;
    [loop]
    for (int i = 1; i <= 16; i++)
    {
        float t = i / 16.0;
        float depression = -VoxelGridVariedBevel(q + ray * t, footprint, jointWidth, bevelWidth, jointDepth, variation, planeCell, cellU, cellV).w / total;
        if (t >= depression) { hi = t; break; }
        lo = t;
    }
    [unroll]
    for (int j = 0; j < 4; j++)
    {
        float t = (lo + hi) * 0.5;
        float depression = -VoxelGridVariedBevel(q + ray * t, footprint, jointWidth, bevelWidth, jointDepth, variation, planeCell, cellU, cellV).w / total;
        if (t >= depression) hi = t; else lo = t;
    }
    float hit = (lo + hi) * 0.5;
    return float3(q + ray * hit, hit);
}

float3 VoxelGridParallax(float2 q, float2 footprint, float jointWidth, float bevelWidth,
    float depthInCells, float3 viewInGrid)
{
    return VoxelGridParallax(q, footprint, jointWidth, bevelWidth, depthInCells, viewInGrid,
        0.25, 0.0, float3(0,0,0), float3(1,0,0), float3(0,1,0));
}

float VoxelGridParallaxWeight(float cameraDistance, float ndotv, float level)
{
    return (1.0 - smoothstep(2.0, 8.0, cameraDistance)) * smoothstep(0.12, 0.25, ndotv)
        * (1.0 - smoothstep(0.0, 1.0, level));
}

// HDRP's Depth Offset is a distance along the view ray, not along the face normal.
float VoxelGridRayDepth(float hit, float normalDepth, float ndotv)
{
    return saturate(hit) * max(normalDepth, 0.0) / max(ndotv, 0.12);
}

// Family-local requires a shared renderer frame (no static batching). UV0/UV3 remain IDs.
void VoxelGridDetailDepth_float(float3 Position, float3 Normal, float3 Tangent, float3 Bitangent,
    float Enabled, float CellSize, float JointWidth, float NormalStrength,
    float RoughnessStrength, float FadeStart, float FadeEnd, float BaseSmoothness,
    float Multiscale, float TargetPixels, float MaxScaleLevels,
    float AnchorMode, float DistanceStart, float DistanceStep,
    float Profile, float BevelWidth, float JointDepth,
    float PomEnabled, float PomMaxDepth,
    float SurfaceHeightVariation,
    out float3 DetailNormalTS, out float DetailSmoothness, out float DepthOffset)
{
    DetailNormalTS = float3(0, 0, 1);
    DetailSmoothness = BaseSmoothness;
    DepthOffset = 0;
#if !defined(SHADERGRAPH_PREVIEW) && !defined(SHADER_STAGE_RAY_TRACING)
    float3 positionRWS = Position;
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
    float3 cellU = u, cellV = v, frameMetres = framePosition;
    float2 q = float2(dot(framePosition, u), dot(framePosition, v));
    if (AnchorMode >= 0.5)
    {
        // Preserve metres under non-uniform (non-sheared) instance scaling.
        float3 worldU = mul(modelToWorld, u), worldV = mul(modelToWorld, v);
        float2 axisScale = max(float2(length(worldU), length(worldV)), 0.000001);
        frameMetres *= float3(length(mul(modelToWorld, float3(1,0,0))),
            length(mul(modelToWorld, float3(0,1,0))), length(mul(modelToWorld, float3(0,0,1))));
        q *= axisScale;
        u = worldU / axisScale.x; v = worldV / axisScale.y;
    }
    q /= max(CellSize, 0.001);
    float3 cell = floor(frameMetres / max(CellSize, 0.001) - frameNormal * 0.0001);
    float3 planeCell = cell - cellU * dot(cell, cellU) - cellV * dot(cell, cellV);
    float variation = min(clamp(SurfaceHeightVariation, 0.0, 0.25), 0.25 - clamp(JointDepth, 0.0, 0.25));
    // Evaluate derivatives before branching. Fade unresolved cells instead of aliasing.
    float2 footprint = max(fwidth(q), 0.0001);
    float weight = saturate(Enabled);
    if (weight <= 0.0001) return;
    float level = 0;
    float maxLevel = floor(clamp(MaxScaleLevels, 0.0, 8.0));
    if (Multiscale >= 1.5)
        level = VoxelGridDistanceLevel(distance(Position, _WorldSpaceCameraPos), DistanceStart, DistanceStep, maxLevel);
    else if (Multiscale >= 0.5)
        level = clamp(log2(max(max(footprint.x, footprint.y) * clamp(TargetPixels, 4.0, 64.0), 1.0)), 0.0, maxLevel);
    // Shadow maps need the recessed surface too: a flat caster shadows its own POM receiver.
    // Use the light's view (also for directional/orthographic shadows), never the player's ray.
#if defined(SHADERPASS) && (SHADERPASS == SHADERPASS_SHADOWS)
    #if defined(_DEPTHOFFSET_ON) && _DEPTHOFFSET_ON
    if (PomEnabled >= 0.5 && Profile >= 0.5 && JointDepth + variation > 0)
    {
        float3 lightView = GetWorldSpaceNormalizeViewDir(positionRWS);
        float ndotl = dot(n, lightView);
        float3 gridLight = float3(dot(lightView, u), dot(lightView, v), ndotl);
        float normalDepth = min((clamp(JointDepth, 0.0, 0.25) + variation) * max(CellSize, 0.001),
            clamp(PomMaxDepth, 0.0, 0.01)) * weight;
        normalDepth = min(normalDepth,
            0.2 * max(CellSize, 0.001) * max(ndotl, 0.12) / max(length(gridLight.xy), 0.000001));
        // Camera-distance/derivative fades only flatten the receiver toward the original face.
        // Keeping the physical caster unfaded prevents shadow resolution from flattening it in front of that receiver.
        float3 hit = VoxelGridParallax(q, float2(0.0001, 0.0001), JointWidth, BevelWidth,
            normalDepth / max(CellSize, 0.001), gridLight, JointDepth, variation, planeCell, cellU, cellV);
        DepthOffset = VoxelGridRayDepth(hit.z, normalDepth, ndotl);
    }
    #endif
#elif !defined(SHADERPASS) || (SHADERPASS != SHADERPASS_LIGHT_TRANSPORT)
    if (PomEnabled >= 0.5 && Profile >= 0.5 && JointDepth + variation > 0 && unity_OrthoParams.w < 0.5)
    {
        float3 view = normalize(_WorldSpaceCameraPos - Position);
        float ndotv = dot(n, view);
        float fade = VoxelGridParallaxWeight(distance(Position, _WorldSpaceCameraPos), ndotv, level);
        float depth = min((clamp(JointDepth, 0.0, 0.25) + variation) * max(CellSize, 0.001), clamp(PomMaxDepth, 0.0, 0.01));
        if (fade > 0.0001 && depth > 0)
        {
            float normalDepth = depth * fade;
            float3 gridView = float3(dot(view, u), dot(view, v), ndotv);
#if defined(_DEPTHOFFSET_ON) && _DEPTHOFFSET_ON
            float visibility = weight;
            if (Multiscale < 0.5)
                visibility *= 1.0 - smoothstep(max(0, FadeStart), max(FadeStart + 0.01, FadeEnd), distance(Position, _WorldSpaceCameraPos));
            // Keep the displaced depth and UV hit on the same ray when its lateral cap is reached.
            normalDepth = min(normalDepth * visibility,
                0.2 * max(CellSize, 0.001) * max(ndotv, 0.12) / max(length(gridView.xy), 0.000001));
#endif
            float3 hit = VoxelGridParallax(q, footprint, JointWidth, BevelWidth, normalDepth / max(CellSize, 0.001),
                gridView, JointDepth, variation, planeCell, cellU, cellV);
            q = hit.xy;
#if defined(_DEPTHOFFSET_ON) && _DEPTHOFFSET_ON
            DepthOffset = VoxelGridRayDepth(hit.z, normalDepth, ndotv);
#endif
        }
    }
#endif
    float3 pattern;
    if (Multiscale >= 0.5)
    {
        // Crossfade two fixed, nested grids instead of stretching the grid with the camera.
        // Distance selection ignores face angle; derivative filtering below still prevents aliasing.
        float lower = floor(level);
        float upper = min(lower + 1.0, maxLevel);
        float lowerScale = exp2(lower), upperScale = exp2(upper);
        float blend = smoothstep(0.0, 1.0, frac(level));
        // Variation belongs to fine cells only; fade it out instead of re-seeding larger grids.
        pattern = lerp(VoxelGridSurfacePattern(q / lowerScale, footprint / lowerScale, JointWidth, Profile, BevelWidth, JointDepth,
                lower < 0.5 ? variation : 0.0, planeCell, cellU, cellV),
            VoxelGridSurfacePattern(q / upperScale, footprint / upperScale, JointWidth, Profile, BevelWidth, JointDepth,
                upper < 0.5 ? variation : 0.0, planeCell, cellU, cellV), blend);
        // At the size cap, filtering still fades detail that can no longer be resolved.
    }
    else
    {
        float cameraDistance = distance(Position, _WorldSpaceCameraPos);
        weight *= 1.0 - smoothstep(max(0, FadeStart), max(FadeStart + 0.01, FadeEnd), cameraDistance);
        pattern = VoxelGridSurfacePattern(q, footprint, JointWidth, Profile, BevelWidth, JointDepth, variation, planeCell, cellU, cellV);
    }
    float3 gradient = u * pattern.x + v * pattern.y;
    gradient -= n * dot(gradient, n);
    float3 detailN = normalize(n - gradient * saturate(NormalStrength) * weight);
    DetailNormalTS = normalize(float3(dot(detailN, normalize(Tangent)),
        dot(detailN, normalize(Bitangent)), dot(detailN, n)));
    DetailSmoothness = saturate(BaseSmoothness - pattern.z * saturate(RoughnessStrength) * weight);
#endif
}

// Production graphs keep the original interface and do not opt into depth writing.
void VoxelGridDetail_float(float3 Position, float3 Normal, float3 Tangent, float3 Bitangent,
    float Enabled, float CellSize, float JointWidth, float NormalStrength,
    float RoughnessStrength, float FadeStart, float FadeEnd, float BaseSmoothness,
    float Multiscale, float TargetPixels, float MaxScaleLevels,
    float AnchorMode, float DistanceStart, float DistanceStep,
    float Profile, float BevelWidth, float JointDepth, float PomEnabled, float PomMaxDepth,
    float SurfaceHeightVariation, out float3 DetailNormalTS, out float DetailSmoothness)
{
    float unusedDepth;
    VoxelGridDetailDepth_float(Position, Normal, Tangent, Bitangent, Enabled, CellSize, JointWidth,
        NormalStrength, RoughnessStrength, FadeStart, FadeEnd, BaseSmoothness, Multiscale,
        TargetPixels, MaxScaleLevels, AnchorMode, DistanceStart, DistanceStep, Profile,
        BevelWidth, JointDepth, PomEnabled, PomMaxDepth, SurfaceHeightVariation,
        DetailNormalTS, DetailSmoothness, unusedDepth);
}

// Existing POM graphs without surface variation retain a uniform height field.
void VoxelGridDetail_float(float3 Position, float3 Normal, float3 Tangent, float3 Bitangent,
    float Enabled, float CellSize, float JointWidth, float NormalStrength,
    float RoughnessStrength, float FadeStart, float FadeEnd, float BaseSmoothness,
    float Multiscale, float TargetPixels, float MaxScaleLevels,
    float AnchorMode, float DistanceStart, float DistanceStep,
    float Profile, float BevelWidth, float JointDepth, float PomEnabled, float PomMaxDepth,
    out float3 DetailNormalTS, out float DetailSmoothness)
{
    VoxelGridDetail_float(Position, Normal, Tangent, Bitangent, Enabled, CellSize, JointWidth,
        NormalStrength, RoughnessStrength, FadeStart, FadeEnd, BaseSmoothness,
        Multiscale, TargetPixels, MaxScaleLevels, AnchorMode, DistanceStart, DistanceStep,
        Profile, BevelWidth, JointDepth, PomEnabled, PomMaxDepth, 0.0, DetailNormalTS, DetailSmoothness);
}

// Bevel graphs without the experimental POM controls retain their original output.
void VoxelGridDetail_float(float3 Position, float3 Normal, float3 Tangent, float3 Bitangent,
    float Enabled, float CellSize, float JointWidth, float NormalStrength,
    float RoughnessStrength, float FadeStart, float FadeEnd, float BaseSmoothness,
    float Multiscale, float TargetPixels, float MaxScaleLevels,
    float AnchorMode, float DistanceStart, float DistanceStep,
    float Profile, float BevelWidth, float JointDepth,
    out float3 DetailNormalTS, out float DetailSmoothness)
{
    VoxelGridDetail_float(Position, Normal, Tangent, Bitangent, Enabled, CellSize, JointWidth,
        NormalStrength, RoughnessStrength, FadeStart, FadeEnd, BaseSmoothness,
        Multiscale, TargetPixels, MaxScaleLevels, AnchorMode, DistanceStart, DistanceStep,
        Profile, BevelWidth, JointDepth, 0.0, 0.003, DetailNormalTS, DetailSmoothness);
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
