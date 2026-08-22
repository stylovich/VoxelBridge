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

// Shader Graph Custom Function entry point.
void SampleGeometryOccupancy_float(float3 PositionWS, out float Occupancy)
{
    Occupancy = (float)SampleGeometryOccupancy(PositionWS);
}

#endif
