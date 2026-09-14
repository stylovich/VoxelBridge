#ifndef VOXEL_BRIDGE_BLOCK_SPOM_INCLUDED
#define VOXEL_BRIDGE_BLOCK_SPOM_INCLUDED
#include "VoxelGridDetail.hlsl"

// Convex clipping keeps cell caps closed, including their side walls and corner bevels.
bool VoxelBlockClipPlane(float3 origin, float3 ray, float3 normal, float limit,
    inout float enter, inout float leave, inout float3 hitNormal)
{
    float speed = dot(normal, ray);
    float remaining = limit - dot(normal, origin);
    if (abs(speed) < 0.0000001) return remaining >= -0.0000001;
    float t = remaining / speed;
    if (speed < 0)
    {
        if (t > enter) { enter = t; hitNormal = normal; }
    }
    else leave = min(leave, t);
    return enter <= leave;
}

bool VoxelBlockBox(float3 origin, float3 ray, float3 minimum, float3 maximum, float bevel,
    out float enter, out float leave, out float3 normal)
{
    float3 halfSize = (maximum - minimum) * 0.5;
    origin -= (maximum + minimum) * 0.5;
    enter = -1e20; leave = 1e20; normal = 0;
    [unroll] for (int axis = 0; axis < 3; axis++)
    {
        float3 n = 0; n[axis] = 1;
        if (!VoxelBlockClipPlane(origin, ray, n, halfSize[axis], enter, leave, normal)) return false;
        if (!VoxelBlockClipPlane(origin, ray, -n, halfSize[axis], enter, leave, normal)) return false;
    }
    if (bevel > 0.0000001)
    {
        // Twelve edge planes and eight corner planes form a closed chamfered cuboid.
        [unroll] for (int pair = 0; pair < 3; pair++)
        [unroll] for (int signs = 0; signs < 4; signs++)
        {
            int a = pair == 2 ? 1 : 0;
            int b = pair == 0 ? 1 : 2;
            float3 n = 0; n[a] = (signs & 1) ? 1 : -1; n[b] = (signs & 2) ? 1 : -1;
            if (!VoxelBlockClipPlane(origin, ray, n, halfSize[a] + halfSize[b] - bevel, enter, leave, normal)) return false;
        }
        [unroll] for (int cornerSigns = 0; cornerSigns < 8; cornerSigns++)
        {
            float3 n = float3((cornerSigns & 1) ? 1 : -1, (cornerSigns & 2) ? 1 : -1, (cornerSigns & 4) ? 1 : -1);
            if (!VoxelBlockClipPlane(origin, ray, n, halfSize.x + halfSize.y + halfSize.z - 2 * bevel,
                enter, leave, normal)) return false;
        }
    }
    normal = normalize(normal);
    return leave >= max(enter, 0);
}

// A solid inner box closes the joints; independently recessed cell caps define the silhouette.
// Contract: aligned box, 2..16 cells per axis, positive dimensions; unit-length ray in metric local space.
bool VoxelBlockTrace(float3 origin, float3 ray, float3 halfSize, float cellSize,
    float jointWidth, float bevelWidth, float jointDepth, float variation, float maxDepth,
    out float hitDistance, out float3 hitNormal, out float jointMask)
{
    hitDistance = 1e20; hitNormal = 0; jointMask = 0;
    float entry, exit; float3 normal;
    if (!VoxelBlockBox(origin, ray, -halfSize, halfSize, 0, entry, exit, normal)) return false;
    float start = max(entry, 0);
    jointDepth = clamp(jointDepth, 0, 0.25);
    variation = min(clamp(variation, 0, 0.25), 0.25 - jointDepth);
    float total = jointDepth + variation;
    float depth = min(total * cellSize, clamp(maxDepth, 0, 0.01));
    if (depth <= 0.0000001)
    {
        hitDistance = start; hitNormal = normal; return true;
    }
    float coreEntry, coreExit; float3 coreNormal;
    if (VoxelBlockBox(origin, ray, -halfSize + depth, halfSize - depth, 0, coreEntry, coreExit, coreNormal))
    {
        hitDistance = max(coreEntry, 0); hitNormal = coreNormal; jointMask = 1;
    }
    float gap = clamp(jointWidth * 0.5, 0, 0.24) * cellSize;
    float epsilon = cellSize * 0.0001;
    float cursor = start;
    // A ray crosses at most 48 cells in the supported 16^3 box; ties advance all affected axes.
    [loop] for (int step = 0; step < 64; step++)
    {
        if (cursor > exit || cursor >= hitDistance) break;
        float3 cell = floor((origin + ray * (cursor + epsilon)) / cellSize);
        float3 cellMin = max(cell * cellSize, -halfSize);
        float3 cellMax = min((cell + 1) * cellSize, halfSize);
        if (any(cellMax <= cellMin)) break;
        float sink = depth * variation / max(total, 0.000001) * VoxelGridCellRandom(cell);
        float3 capMin = cellMin + float3(cellMin.x <= -halfSize.x + epsilon ? sink : gap,
            cellMin.y <= -halfSize.y + epsilon ? sink : gap, cellMin.z <= -halfSize.z + epsilon ? sink : gap);
        float3 capMax = cellMax - float3(cellMax.x >= halfSize.x - epsilon ? sink : gap,
            cellMax.y >= halfSize.y - epsilon ? sink : gap, cellMax.z >= halfSize.z - epsilon ? sink : gap);
        float3 capHalf = (capMax - capMin) * 0.5;
        float bevel = min(clamp(bevelWidth, 0, 0.2) * cellSize,
            min(depth - sink, min(capHalf.x, min(capHalf.y, capHalf.z)) * 0.5));
        float capEntry, capExit; float3 capNormal;
        if (VoxelBlockBox(origin, ray, capMin, capMax, bevel, capEntry, capExit, capNormal))
        {
            float t = max(capEntry, 0);
            if (t < hitDistance)
            {
                hitDistance = t; hitNormal = capNormal;
                jointMask = max(abs(capNormal.x), max(abs(capNormal.y), abs(capNormal.z))) < 0.99 ? 1 : 0;
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

// Experimental unit-cube proxy only. Arbitrary converted meshes need occupancy/boundary data first.
void VoxelBlockSpom_float(float3 Position, float3 Normal, float3 Tangent, float3 Bitangent,
    float Enabled, float CellSize, float JointWidth, float NormalStrength,
    float RoughnessStrength, float FadeStart, float FadeEnd, float BaseSmoothness,
    float Multiscale, float TargetPixels, float MaxScaleLevels,
    float AnchorMode, float DistanceStart, float DistanceStep,
    float Profile, float BevelWidth, float JointDepth, float PomEnabled, float PomMaxDepth,
    float SurfaceHeightVariation, out float3 DetailNormalTS, out float DetailSmoothness,
    out float DepthOffset, out float Alpha)
{
    Alpha = 1;
    VoxelGridDetailDepth_float(Position, Normal, Tangent, Bitangent, Enabled, CellSize, JointWidth,
        NormalStrength, RoughnessStrength, FadeStart, FadeEnd, BaseSmoothness, Multiscale, TargetPixels,
        MaxScaleLevels, AnchorMode, DistanceStart, DistanceStep, Profile, BevelWidth, JointDepth,
        PomEnabled, PomMaxDepth, SurfaceHeightVariation, DetailNormalTS, DetailSmoothness, DepthOffset);
#if !defined(SHADERGRAPH_PREVIEW) && !defined(SHADER_STAGE_RAY_TRACING) && defined(_DEPTHOFFSET_ON) && _DEPTHOFFSET_ON
    #if !defined(SHADERPASS) || SHADERPASS != SHADERPASS_LIGHT_TRANSPORT
    if (Enabled < 0.5 || PomEnabled < 0.5 || Profile < 0.5 || AnchorMode < 0.5 || Multiscale >= 0.5) return;
    float3x3 model = (float3x3)GetObjectToWorldMatrix();
    float3 axisX = mul(model, float3(1,0,0)), axisY = mul(model, float3(0,1,0)), axisZ = mul(model, float3(0,0,1));
    float3 scales = float3(length(axisX), length(axisY), length(axisZ));
    if (any(scales < 0.000001)) return;
    axisX /= scales.x; axisY /= scales.y; axisZ /= scales.z;
    if (abs(dot(axisX, axisY)) + abs(dot(axisX, axisZ)) + abs(dot(axisY, axisZ)) > 0.0001) return;
    float3 halfSize = scales * 0.5;
    float3 halfCells = halfSize / max(CellSize, 0.001);
    if (any(halfCells < 1) || any(halfCells > 8) || any(abs(halfCells - round(halfCells)) > 0.0001)) return;
    float3 origin = TransformWorldToObject(Position) * scales;
    float3 worldRay = -GetWorldSpaceNormalizeViewDir(Position);
    float3 ray = float3(dot(worldRay, axisX), dot(worldRay, axisY), dot(worldRay, axisZ));
    float distance, joint; float3 normal;
    bool hit = VoxelBlockTrace(origin, ray, halfSize, max(CellSize, 0.001), JointWidth, BevelWidth,
        JointDepth, SurfaceHeightVariation, PomMaxDepth, distance, normal, joint);
    if (!hit) { Alpha = 0; DepthOffset = 0; return; }
    DepthOffset = max(distance, 0);
    float3 worldNormal = axisX * normal.x + axisY * normal.y + axisZ * normal.z;
    DetailNormalTS = normalize(float3(dot(worldNormal, normalize(Tangent)),
        dot(worldNormal, normalize(Bitangent)), dot(worldNormal, normalize(Normal))));
    DetailSmoothness = saturate(BaseSmoothness - joint * saturate(RoughnessStrength));
    #endif
#endif
}
#endif
