# Dynamic GI and Voxel Bridge tools for Unity

This repository intentionally contains only the portable source of the Dynamic GI
prototype and the Voxel Bridge editor tool. The local Polygon test environment,
scenes, imported packages, `Library`, voxel exports, and generated lighting data are
excluded from version control.

## Tracked module

Copy `Assets/DynamicGI` and `Assets/DynamicGI.meta` into another Unity project to move
the prototype. The current implementation targets Unity 6000.3.21f1 with HDRP 17.3.0.
See `Assets/DynamicGI/README.md` for setup, architecture, validation, and limitations.

Copy `Assets/VoxelBridge` and `Assets/VoxelBridge.meta` to reuse the mesh-to-MagicaVoxel
workflow. Its optional Voxel Importer compatibility patch is stored as source
transformation code; the Asset Store package itself is deliberately not tracked.
See `Assets/VoxelBridge/README.md` for usage and maintenance.

The Test3-specific editor commands remain useful as examples, but the runtime Geometry
Field, tiled Sky Visibility field, six-direction local Radiance Field, contributors,
camera-centred Radiance Clipmap, bounded diffuse propagation, compute shaders, shader
sampling/provider APIs, the optional HDRP stock-material bridge, and debug renderers do
not depend on the Polygon assets or APV bake data.
