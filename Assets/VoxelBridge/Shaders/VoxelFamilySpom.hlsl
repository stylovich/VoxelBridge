#ifndef VOXEL_BRIDGE_FAMILY_SPOM_INCLUDED
#define VOXEL_BRIDGE_FAMILY_SPOM_INCLUDED
#include "VoxelBlockSpom.hlsl"

// RG: ColorID/SurfaceID, B: exposed faces (-X,+X,-Y,+Y,-Z,+Z), A: opaque occupancy.
// All chunks of a family share this point-sampled, linear volume.
bool VoxelFamilyTrace(UnityTexture3D volume, UnityTexture2D surfaces, int3 size,
    float3 origin, float3 ray, float cellSize, float3 seedOffset,
    float jointWidth, float bevelWidth, float jointDepth, float maxDepth,
    out float hitDistance, out float3 hitNormal, out float2 hitIds, out float jointMask)
{
    hitDistance = 1e20; hitNormal = 0; hitIds = 0; jointMask = 0;
    float entry, exit; float3 normal;
    if (!VoxelBlockBox(origin, ray, 0, (float3)size * cellSize, 0, entry, exit, normal)) return false;
    float cursor = max(entry, 0);
    float epsilon = cellSize * 0.0001;
    float gap = clamp(jointWidth * 0.5, 0, 0.24) * cellSize;
    jointDepth = clamp(jointDepth, 0, 0.25);
    [loop] for (int step = 0; step < 772; step++)
    {
        if (step >= size.x + size.y + size.z + 3 || cursor > exit || cursor >= hitDistance) break;
        int3 cell = (int3)floor((origin + ray * (cursor + epsilon)) / cellSize);
        if (any(cell < 0) || any(cell >= size)) break;
        float3 cellMin = (float3)cell * cellSize;
        float3 cellMax = cellMin + cellSize;
        float4 data = volume.tex.Load(int4(cell, 0));
        if (data.a > 0.5)
        {
            float2 ids = round(data.rg * 255);
            float variation = 0;
            if (surfaces.texelSize.w > 1.5)
                variation = saturate(surfaces.tex.SampleLevel(surfaces.samplerstate,
                    float2((ids.y + 0.5) * surfaces.texelSize.x, 1.5 * surfaces.texelSize.y), 0).r) * 0.25;
            variation = min(variation, 0.25 - jointDepth);
            float total = jointDepth + variation;
            float depth = min(total * cellSize, clamp(maxDepth, 0, 0.01));
            uint faces = (uint)round(data.b * 255);
            float3 exposedMin = float3((faces & 1) != 0, (faces & 4) != 0, (faces & 16) != 0);
            float3 exposedMax = float3((faces & 2) != 0, (faces & 8) != 0, (faces & 32) != 0);
            // Neighbouring cores touch at occupied faces, including across chunk boundaries.
            // Only a face bordering empty/transparent space is recessed.
            float coreEntry, coreExit; float3 coreNormal;
            if (VoxelBlockBox(origin, ray, cellMin + exposedMin * depth, cellMax - exposedMax * depth,
                0, coreEntry, coreExit, coreNormal))
            {
                float t = max(coreEntry, 0);
                if (t < hitDistance) { hitDistance = t; hitNormal = coreNormal; hitIds = ids; jointMask = depth > 0 ? 1 : 0; }
            }
            float sink = depth * variation / max(total, 0.000001) * VoxelGridCellRandom(seedOffset + cell);
            float3 capMin = cellMin + lerp(gap.xxx, sink.xxx, exposedMin);
            float3 capMax = cellMax - lerp(gap.xxx, sink.xxx, exposedMax);
            float3 halfSize = (capMax - capMin) * 0.5;
            float bevel = min(clamp(bevelWidth, 0, 0.2) * cellSize,
                min(depth - sink, min(halfSize.x, min(halfSize.y, halfSize.z)) * 0.5));
            float capEntry, capExit; float3 capNormal;
            if (VoxelBlockBox(origin, ray, capMin, capMax, bevel, capEntry, capExit, capNormal))
            {
                float t = max(capEntry, 0);
                if (t < hitDistance)
                {
                    hitDistance = t; hitNormal = capNormal; hitIds = ids;
                    jointMask = max(abs(capNormal.x), max(abs(capNormal.y), abs(capNormal.z))) < 0.99 ? 1 : 0;
                }
            }
        }
        float3 next;
        [unroll] for (int axis = 0; axis < 3; axis++)
            next[axis] = abs(ray[axis]) < 0.0000001 ? 1e20 :
                ((ray[axis] > 0 ? cellMax[axis] : cellMin[axis]) - origin[axis]) / ray[axis];
        cursor = min(next.x, min(next.y, next.z));
    }
    return hitDistance <= exit;
}

// Native voxel geometry is the proxy. Meshes and occupancy must come from the same LOD0 snapshot.
void VoxelFamilySpom_float(float3 Position, float3 Normal, float3 Tangent, float3 Bitangent,
    UnityTexture3D Volume, float3 GridOrigin, float3 GridSize, float VoxelSize,
    UnityTexture2D SurfacePalette, float Enabled, float PomEnabled, float JointWidth,
    float BevelWidth, float JointDepth, float MaxDepth, float RoughnessStrength,
    float4 OriginalColor, float4 OriginalSurface,
    out float3 DetailNormalTS, out float DetailSmoothness, out float DepthOffset, out float Alpha,
    out float4 HitColor, out float4 HitSurface)
{
    DetailNormalTS = float3(0,0,1); DepthOffset = 0; Alpha = 1;
    HitColor = OriginalColor; HitSurface = OriginalSurface;
    DetailSmoothness = SurfacePalette.tex.SampleLevel(SurfacePalette.samplerstate,
        float2((OriginalSurface.x + 0.5) * SurfacePalette.texelSize.x, 0.5 * SurfacePalette.texelSize.y), 0).g;
#if !defined(SHADERGRAPH_PREVIEW) && !defined(SHADER_STAGE_RAY_TRACING) && defined(_DEPTHOFFSET_ON) && _DEPTHOFFSET_ON
    #if !defined(SHADERPASS) || SHADERPASS != SHADERPASS_LIGHT_TRANSPORT
    if (Enabled < 0.5 || PomEnabled < 0.5 || VoxelSize <= 0 || any(GridSize < 1) || any(GridSize > 256)) return;
    float3x3 model = (float3x3)GetObjectToWorldMatrix();
    float3 x = mul(model, float3(1,0,0)), y = mul(model, float3(0,1,0)), z = mul(model, float3(0,0,1));
    // The first family prototype requires normalized, non-reflected, unsheared transforms.
    if (abs(length(x)-1) + abs(length(y)-1) + abs(length(z)-1) > 0.0001 ||
        abs(dot(x,y)) + abs(dot(x,z)) + abs(dot(y,z)) > 0.0001 || dot(cross(x,y),z) < 0.9999) return;
    float3 origin = TransformWorldToObject(Position) - GridOrigin;
    float3 worldRay = -GetWorldSpaceNormalizeViewDir(Position);
    float3 ray = float3(dot(worldRay,x), dot(worldRay,y), dot(worldRay,z));
    float distance, joint; float3 normal; float2 ids;
    if (!VoxelFamilyTrace(Volume, SurfacePalette, (int3)round(GridSize), origin, ray, VoxelSize,
        floor(GridOrigin / VoxelSize), JointWidth, BevelWidth, JointDepth, MaxDepth, distance, normal, ids, joint))
    { Alpha = 0; return; }
    DepthOffset = max(distance, 0);
    HitColor = float4(ids.x,0,0,0); HitSurface = float4(ids.y,0,0,0);
    float3 worldNormal = x * normal.x + y * normal.y + z * normal.z;
    DetailNormalTS = normalize(float3(dot(worldNormal,normalize(Tangent)),
        dot(worldNormal,normalize(Bitangent)), dot(worldNormal,normalize(Normal))));
    DetailSmoothness = saturate(SurfacePalette.tex.SampleLevel(SurfacePalette.samplerstate,
        float2((ids.y + 0.5) * SurfacePalette.texelSize.x, 0.5 * SurfacePalette.texelSize.y), 0).g - joint * saturate(RoughnessStrength));
    #endif
#endif
}
#endif
