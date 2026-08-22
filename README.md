# Dynamic GI voxel prototype for Unity

This repository intentionally contains only the portable source of the Dynamic GI
prototype. The local Polygon test environment, scenes, imported packages, `Library`,
and generated lighting data are excluded from version control.

## Tracked module

Copy `Assets/DynamicGI` and `Assets/DynamicGI.meta` into another Unity project to move
the prototype. The current implementation targets Unity 6000.3.21f1 with HDRP 17.3.0.
See `Assets/DynamicGI/README.md` for setup, architecture, validation, and limitations.

The Test3-specific editor commands remain useful as examples, but the runtime Geometry
Field, contributors, compute shaders, shader sampling API, and debug renderer do not
depend on the Polygon assets or APV bake data.
