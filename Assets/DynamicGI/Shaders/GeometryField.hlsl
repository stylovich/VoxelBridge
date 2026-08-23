#ifndef DYNAMIC_GI_GEOMETRY_FIELD_INCLUDED
#define DYNAMIC_GI_GEOMETRY_FIELD_INCLUDED

// Runtime-populated sparse geometry resources. Base and dynamic payloads are
// deliberately separate so a local overlay can be cleared/rebuilt without a bake.
StructuredBuffer<uint> _DynamicGI_BaseOccupancy;
StructuredBuffer<uint> _DynamicGI_DynamicOccupancy;
StructuredBuffer<int4> _DynamicGI_GeometryPageTable;

float3 _DynamicGI_GeometryFieldOrigin;
float3 _DynamicGI_GeometryFieldSize;
float _DynamicGI_GeometryVoxelSize;
float _DynamicGI_GeometryBrickWorldSize;
int _DynamicGI_GeometryBrickResolution;
int _DynamicGI_GeometryWordsPerBrick;
int _DynamicGI_GeometryPageTableCapacity;
int _DynamicGI_GeometryFieldAvailable;

uint DynamicGIHashBrickCoordinate(int3 coordinate)
{
    uint hash = (uint)coordinate.x * 73856093u;
    hash ^= (uint)coordinate.y * 19349663u;
    hash ^= (uint)coordinate.z * 83492791u;
    hash ^= hash >> 16;
    hash *= 0x7feb352du;
    hash ^= hash >> 15;
    hash *= 0x846ca68bu;
    hash ^= hash >> 16;
    return hash;
}

int DynamicGIFindGeometryBrick(int3 coordinate)
{
    if (_DynamicGI_GeometryFieldAvailable == 0 || _DynamicGI_GeometryPageTableCapacity <= 0)
        return -1;

    uint tableMask = (uint)(_DynamicGI_GeometryPageTableCapacity - 1);
    uint tableIndex = DynamicGIHashBrickCoordinate(coordinate) & tableMask;

    [loop]
    for (int probe = 0; probe < _DynamicGI_GeometryPageTableCapacity; probe++)
    {
        int4 entry = _DynamicGI_GeometryPageTable[tableIndex];
        if (entry.w == 0)
            return -1;

        if (all(entry.xyz == coordinate))
            return entry.w - 1;

        tableIndex = (tableIndex + 1u) & tableMask;
    }

    return -1;
}

int3 DynamicGIGetGeometryVoxelResolution()
{
    return (int3)ceil(_DynamicGI_GeometryFieldSize / _DynamicGI_GeometryVoxelSize);
}

uint DynamicGIReadGeometryVoxel(int3 globalVoxel)
{
    int3 fieldResolution = DynamicGIGetGeometryVoxelResolution();
    if (any(globalVoxel < 0) || any(globalVoxel >= fieldResolution))
        return 0u;

    int resolution = _DynamicGI_GeometryBrickResolution;
    uint brickShift = firstbithigh((uint)resolution);
    int3 brickCoordinate = globalVoxel >> brickShift;
    int3 localVoxel = globalVoxel & (resolution - 1);
    int brickSlot = DynamicGIFindGeometryBrick(brickCoordinate);
    if (brickSlot < 0)
        return 0u;

    int linearIndex = localVoxel.x + resolution * (localVoxel.y + resolution * localVoxel.z);
    int wordIndex = brickSlot * _DynamicGI_GeometryWordsPerBrick + (linearIndex >> 5);
    uint bitMask = 1u << (linearIndex & 31);
    return ((_DynamicGI_BaseOccupancy[wordIndex] | _DynamicGI_DynamicOccupancy[wordIndex]) & bitMask) != 0u;
}

uint SampleGeometryOccupancy(float3 positionWS)
{
    float3 relative = positionWS - _DynamicGI_GeometryFieldOrigin;
    if (any(relative < 0.0) || any(relative >= _DynamicGI_GeometryFieldSize))
        return 0u;

    return DynamicGIReadGeometryVoxel((int3)floor(relative / _DynamicGI_GeometryVoxelSize));
}

// Exact grid traversal shared by sky visibility, sun injection, and future
// radiance propagation. Returns true when an occupied surface voxel is hit.
bool DynamicGITraceGeometryDDA(
    float3 rayOriginWS,
    float3 rayDirectionWS,
    float maximumDistance,
    int maximumSteps)
{
    float3 relative = rayOriginWS - _DynamicGI_GeometryFieldOrigin;
    if (any(relative < 0.0) || any(relative >= _DynamicGI_GeometryFieldSize))
        return false;

    int3 fieldResolution = DynamicGIGetGeometryVoxelResolution();
    int3 voxel = clamp((int3)floor(relative / _DynamicGI_GeometryVoxelSize), 0, fieldResolution - 1);
    if (DynamicGIReadGeometryVoxel(voxel) != 0u)
        return true;

    int3 stepDirection = int3(
        rayDirectionWS.x >= 0.0 ? 1 : -1,
        rayDirectionWS.y >= 0.0 ? 1 : -1,
        rayDirectionWS.z >= 0.0 ? 1 : -1);
    float3 safeDirection = float3(
        abs(rayDirectionWS.x) > 1e-6 ? rayDirectionWS.x : (rayDirectionWS.x >= 0.0 ? 1e-6 : -1e-6),
        abs(rayDirectionWS.y) > 1e-6 ? rayDirectionWS.y : (rayDirectionWS.y >= 0.0 ? 1e-6 : -1e-6),
        abs(rayDirectionWS.z) > 1e-6 ? rayDirectionWS.z : (rayDirectionWS.z >= 0.0 ? 1e-6 : -1e-6));

    float3 nextBoundaryWS = _DynamicGI_GeometryFieldOrigin + float3(
        stepDirection.x > 0 ? voxel.x + 1 : voxel.x,
        stepDirection.y > 0 ? voxel.y + 1 : voxel.y,
        stepDirection.z > 0 ? voxel.z + 1 : voxel.z) * _DynamicGI_GeometryVoxelSize;
    float3 nextCrossing = (nextBoundaryWS - rayOriginWS) / safeDirection;
    float3 crossingDelta = _DynamicGI_GeometryVoxelSize / abs(safeDirection);

    [loop]
    for (int stepIndex = 0; stepIndex < maximumSteps; stepIndex++)
    {
        float nextDistance = min(nextCrossing.x, min(nextCrossing.y, nextCrossing.z));
        if (nextDistance > maximumDistance)
            return false;

        bool3 crossingMask = nextCrossing <= nextDistance + 1e-5;
        voxel += stepDirection * int3(crossingMask);
        nextCrossing += crossingDelta * float3(crossingMask);

        if (any(voxel < 0) || any(voxel >= fieldResolution))
            return false;
        if (DynamicGIReadGeometryVoxel(voxel) != 0u)
            return true;
    }

    return false;
}

// Shader Graph Custom Function entry point.
void SampleGeometryOccupancy_float(float3 PositionWS, out float Occupancy)
{
    Occupancy = (float)SampleGeometryOccupancy(PositionWS);
}

#endif
