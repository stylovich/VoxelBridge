# Dynamic GI prototype — Phases 1–7 Geometry, Visibility, Clipmaps, Sun, and Emissives

This folder contains the portable runtime GI prototype. It does not replace or modify
HDRP APV, HTrace SSGI, or HTrace AO.
Phase 3 consumes the Geometry Field but remains an independent ambient-accessibility
signal; no existing indirect-lighting result is modified automatically.

## Quick setup

1. In a scene, choose **GameObject > Dynamic GI > World Geometry Field**.
2. Position the component and configure its world-space `Field Center`, `Field Size`,
   `Voxel Size`, brick resolution, capacity, and geometry `Layer Mask`.
3. Use **Rebuild All**, then enable the desired toggles in `GeometryFieldDebug`.
4. Put `GIGeometryContributor` on doors, replaceable buildings, or runtime/mod roots.
   Choose `Base` or `Dynamic`. Call `NotifyGeometryChanged()` after changing a mesh,
   renderer hierarchy, enabled state, or a transform not covered by automatic tracking.

Layer-mask discovery happens only when the field is initialized or its source registry
changes. There is no `FindObjectsByType` call in the update loop.

To add Phase 3, put `WorldSkyVisibilityField` and `SkyVisibilityDebug` on the same
GameObject as `WorldGeometryField`. The GameObject menu command
**Dynamic GI > Sky Visibility Field** adds both components to the selected field.

## Phase 2 visualization

`GeometryFieldDebug` provides independent toggles for field bounds, active bricks,
recently rebuilt regions, the camera neighborhood, resolution/statistics, occupied
voxels, and empty voxels. Occupied/empty cubes are generated with a compute pass and
rendered with GPU instancing; no voxel GameObjects are created.

The custom inspector includes **Occupied**, **Empty**, **Both**, and **Voxels Off**
presets. Its live readout reports the field resolution and the debug sample stride.
A stride greater than one means the visualization was spatially subsampled to respect
`Maximum Voxel Instances`; reduce the camera radius or raise that cap when inspecting
thin surfaces.

When **Query Occupancy At Camera** is enabled, the Scene view shows a small wire cube
at the queried position: green is occupied, blue is empty, and red is a GPU readback
error. In Edit Mode the debug center follows the last active Scene view camera when no
explicit target camera is assigned.

### Test3 laboratory

`Assets/Scenes/Test3.unity` contains the Phase 2 field configured as a local laboratory:

- Bounds: center `(-24, -4, -20)`, size `128 x 32 x 128` metres.
- Voxel resolution: `256 x 64 x 256` at `0.5 m`.
- Brick resolution/capacity: `16³`, up to `4096` sparse bricks.
- Camera neighborhood: `12 m`, up to `131072` debug instances.
- Layer 2 (`Ignore Raycast`) is excluded because Test3 uses it for presentation helpers.

The scene keeps its Polygon environment, APV, HTrace SSGI, and HTrace AO unchanged.
Its serialized camera is inactive, so Edit Mode intentionally uses the Scene view
camera fallback.

The setup and validation can be reproduced from Unity menus under
**Tools > Dynamic GI > Phase 2**, or headlessly with Unity CLI:

```powershell
unity run . -- -executeMethod DynamicGI.Editor.GeometryFieldPhase2SceneSetup.ValidateTest3 -logFile -
```

## Phase 3 sky visibility / occlusion

`WorldSkyVisibilityField` stores one scalar per low-resolution world-space sample:

- `0` means the configured upper hemisphere is structurally enclosed.
- `1` means it is open to the sky within the configured trace distance.

Each sample launches 4–64 configurable, deterministically rotated Fibonacci directions
(24 by default). Rays traverse the sparse occupancy field with exact 3D DDA on the GPU;
there are no CPU `Physics.Raycast` calls. Occupied samples are forced to zero. The result
is a trilinear 3D `R8_UNorm` texture, with an automatic `R16_SFloat` fallback when R8
unordered writes are unavailable.

The volume is divided into update tiles. Geometry reset events invalidate the entire
field, while rebuilt brick events invalidate only the conservative region from which
the changed geometry can occlude upper-hemisphere rays. Duplicate tiles are coalesced,
and the field waits for pending geometry bricks before consuming occupancy. This keeps
geometry and occlusion updates ordered without rebuilding them every frame.

`SkyVisibilityDebug` renders small GPU-instanced samples around the camera or Scene
view. Red means enclosed, cyan means open, and intermediate colors show partial access.
Separate toggles expose field bounds, dirty tiles, recently updated tiles, the camera
neighborhood, live statistics, and an asynchronous GPU query at the camera. The sample
cubes are intentionally much smaller than their cells so the volume remains readable.

For Test3, run **Tools > Dynamic GI > Phase 3 > Configure Test3 Sky Visibility**.
The laboratory preset uses `128 x 32 x 128` samples at 1 m, 16 rays, a 32 m trace,
`8³` tiles, and eight tile updates per frame. A full R8 volume is about 0.5 MiB.

The generated-room validation checks an enclosed room, an outdoor point, a local door
invalidation, an opened roof, trilinear GPU queries, and debug instance generation:

```powershell
unity run . -- -executeMethod DynamicGI.Editor.SkyVisibilityPhase3Validation.Run -logFile -
unity run . -- -executeMethod DynamicGI.Editor.SkyVisibilityPhase3SceneSetup.ValidateTest3 -logFile -
```

### Shader sampling

Include `Shaders/SkyVisibilityField.hlsl` and call:

```hlsl
float visibility = SampleSkyVisibility(positionWS);
```

`positionWS` must be absolute world space. Sampling outside the configured volume, or
before it is available, returns `1` (open) as a conservative fallback. For Shader Graph,
use a File-mode Custom Function named `SampleSkyVisibility`, with `Vector3 PositionWS`
and `Float Visibility`.

This value represents sky/ambient accessibility and local structural darkening. It
must not indiscriminately multiply the complete APV indirect result, because that would
also erase valid baked sun bounces. Material integration is intentionally deferred until
the dynamic radiance field exists and the two indirect sources can be combined explicitly.

## Phase 4 local directional radiance

`WorldRadianceField` is a local, regular probe grid independent from APV. Each probe
stores six RGB diffuse-irradiance values corresponding to surface normals `+X`, `-X`,
`+Y`, `-Y`, `+Z`, and `-Z`. Six directions were chosen instead of SH L1 for this first
iteration because individual lobes are immediately inspectable, injection is simple,
and wall/window errors are easier to diagnose numerically.

The current injection pass combines:

- sky color/intensity multiplied by Phase 3 sky accessibility;
- one realtime directional light;
- exact Geometry Field DDA visibility from each probe toward the Sun.

The six volumes use trilinear `RGBA16F` 3D textures (`RGBA32F` fallback), update in
deduplicated tiles, and only recalculate after lighting/source changes or dependent
geometry/sky regions change. A continuously moving Sun requeues tiles fairly instead
of clearing the pending queue every frame. Camera following is available with snapped
origins, but the Phase-4 reference field performs a full local reset when its origin
moves. `WorldRadianceClipmap` is the scalable Phase-5 replacement for that mode.

This is direct source injection into a radiance representation, not final multi-bounce
GI. Neighbor propagation, surface albedo feedback, and reconvergence are intentionally
reserved for Phase 8.

### Radiance debug and numeric values

`RadianceFieldDebug` renders every selected probe as a small GPU-instanced cube. Its
color is exposure-mapped from Average, Maximum, or one of the six directional values.
It can show field bounds, dirty/recent tiles, the debug neighborhood, Sun direction,
probe wire gizmos, statistics, and numeric luminance labels.

Numeric values come from a throttled asynchronous GPU readback of the debug buffer.
No probe GameObjects or CPU raycasts are created. By default labels use a horizontal
slice at the detailed-query marker, making every probe value in that slice readable;
disable `Numeric Horizontal Slice Only` to inspect the full 3D selection. The detailed
query label reports all six RGB values for one marker.

### TestGI east-window laboratory

Run **Tools > Dynamic GI > Phase 4 > Configure TestGI East Window Room**. The command
idempotently creates a `10 x 5 x 8 m` room with a real opening in the east (`+X`) wall,
positions the HDRP Sun so it enters through that opening, adds lit/blocked markers, and
configures all three fields:

- Geometry: `0.25 m` voxels.
- Sky visibility: `0.5 m`, 24 rays.
- Radiance: `24 x 12 x 20` (`5760`) probes at `0.5 m`, six RGB directions.
- Test volume memory: approximately `270 KiB` for radiance.

The scene-specific geometry/materials remain local and ignored by Git. The portable
setup command recreates them in any project containing `Assets/Scenes/TestGI.unity`.
Use **Validate TestGI East Window Room** for an in-scene GPU smoke test, or run the
asset-independent generated-room validation:

```powershell
unity run . -- -executeMethod DynamicGI.Editor.RadiancePhase4Validation.Run -logFile -
unity run . -- -executeMethod DynamicGI.Editor.RadiancePhase4SceneSetup.ValidateTestGI -logFile -
```

### Radiance shader sampling

Include `Shaders/RadianceField.hlsl` and call:

```hlsl
float3 dynamicDiffuse = SampleDynamicGI(positionWS, normalWS);
```

The function trilinearly samples all relevant directional volumes and blends the three
signed axes selected by the world-space normal. It automatically prefers the active
Phase-5 clipmap and falls back to the Phase-4 local volume. Outside every available
volume it returns zero. It is not wired into HDRP/APV materials automatically yet; that
explicit provider/compositing integration belongs to Phase 10.

## Phase 5 radiance clipmap / cascades

`WorldRadianceClipmap` owns up to four camera/player-centred `RadianceCascade` levels.
Each level keeps the same six-direction RGB representation as Phase 4, but has its own
configurable resolution, spacing, tile size, update interval, and tile budget. Default
spacings are `0.5 m`, `1 m`, and `2 m`; they are laboratory values, not fixed city-scale
recommendations.

Origins snap to whole update tiles. Crossing a boundary rotates a three-dimensional
toroidal ring offset instead of copying textures: probes still inside the new window
keep their physical texels, and only the entering slabs become dirty. Pending work uses
global tile coordinates, so scrolling drops only tiles that actually left the window.
A teleport larger than a cascade resets that level completely. Geometry and sky events
continue to invalidate only intersecting cascade tiles.

The shader include performs manual trilinear loads because hardware filtering cannot
cross a wrapped toroidal seam correctly. It selects the finest cascade containing the
sample and blends toward the next level near its outer bounds. Cascade resolutions and
tile resolutions are normalized to powers of two, allowing wrapped coordinates to use
a bit mask.

`RadianceClipmapDebug` provides:

- independently colored bounds for all cascades;
- dirty and recently updated tiles for the selected cascade;
- GPU-instanced directional/average probe values;
- a horizontal slice of numeric luminance labels and a six-direction detailed query;
- ring offsets, exposed/recycled probes, update counts, estimated memory, and CPU time.

For compute passes that include `RadianceField.hlsl`, call
`WorldRadianceClipmap.BindSamplingResources(shader, kernel)` before dispatch. Material
shaders receive the same resources as globals and call `SampleDynamicGI` directly.

### TestGI Phase-5 laboratory

Run **Tools > Dynamic GI > Phase 5 > Configure TestGI Radiance Clipmap**. It recreates
the east-window room, disables the Phase-4 local field without removing it, follows the
Main Camera, and configures C0 as `16 x 16 x 16` at `0.5 m` plus two `16 x 8 x 16`
cascades at `1/2 m`. The taller near cascade covers the laboratory floor and ceiling.
The selected debug view is Cascade 0, `+X`, with numeric labels. The portable validation checks
snapping, partial slab invalidation, toroidal recycling, large teleports, manual shader
sampling, fine/coarse blending, and debug readback:

```powershell
unity run . -- -executeMethod DynamicGI.Editor.RadiancePhase5Validation.Run -logFile -
unity run . -- -executeMethod DynamicGI.Editor.RadiancePhase5SceneSetup.ValidateTestGI -logFile -
```

The generated validation moves C0 by one 1 m tile: 256 of 2048 probes are exposed and
1792 are recycled. In TestGI, the east-window probe must remain lit while the probe
behind the solid wall stays dark.

## Phase 6 dynamic Sun injection

Every radiance tile injects the current directional Sun on the GPU. Probe-to-Sun
visibility uses the same exact 3D DDA traversal of `WorldGeometryField` as the earlier
validation field; no CPU raycasts or light bake are involved. Direct irradiance is
stored in the six directional RGB lobes, so changing the Light transform, linear color,
intensity, enabled state, or active state updates the runtime field automatically.

`WorldRadianceClipmap` accumulates small changes against its last accepted state. The
angular, Sun-radiance, and sky-radiance thresholds are configurable, avoiding continuous
full invalidations from insignificant day/night-controller noise. A meaningful change
increments `Sun Revision` and invalidates every cascade, but processing remains ordered
near-to-far and respects each cascade's update interval and tile budget. Already-pending
tiles are deduplicated, allowing the result to converge without recalculating every probe
in one frame. Custom controllers may call `ForceLightingRefresh()` when an immediate new
revision is required.

When `Fade Sun Below Horizon` is enabled, direct radiance fades across the configured
horizon angle and becomes zero when the direction-to-Sun falls below the world-space
horizon (`direction.y <= 0`), even if an external day/night controller
leaves the directional Light intensity nonzero. Sky accessibility remains independent,
so nighttime probes can retain ambient sky contribution.

### Ground and ceiling debug slices

`RadianceClipmapDebug` can display three simultaneous world-space numeric slices using
one shared GPU readback:

- the detailed-query height (white);
- a configurable ground height (amber);
- a configurable ceiling height (violet).

Each slice has an independent toggle and a wire-plane guide. The live cascade annotation
reports valid query/ground/ceiling sample counts. TestGI uses `0.25 m` for the ground and
`4.25 m` for the interior ceiling slice; these are debug heights, not global assumptions
about a future city terrain.

Run **Tools > Dynamic GI > Phase 6 > Configure TestGI Sun Injection**, then use
**Validate TestGI Sun Cycle**, or run both headlessly:

```powershell
unity run . -- -executeMethod DynamicGI.Editor.RadiancePhase6SceneSetup.ConfigureTestGI -logFile -
unity run . -- -executeMethod DynamicGI.Editor.RadiancePhase6SceneSetup.ValidateTestGI -logFile -
```

The validation checks east-window occlusion, intensity scaling, colored sunlight,
east-to-west rotation, below-horizon night, per-frame cascade budgeting, and all three
numeric slices. Direct Sun is still source injection rather than diffuse neighbor
propagation; actual interior bounce remains Phase 8.

## Phase 7 runtime emissive injection

`GIEmissiveContributor` is the content/mod-facing contract for diffuse emissive
sources. Attach it to an object, assign its renderers, emission color/intensity,
influence range, and maximum cascade. The component registers itself automatically;
content authors never place or reference probes. Runtime code may call `Configure`,
`SetContributionEnabled`, or `NotifyEmissionChanged` after changing source data.

`WorldRadianceClipmap` uploads active contributors to a fixed-capacity structured GPU
buffer only when the registry changes. Each updated probe tests contributors within
range, performs one Geometry Field DDA toward each applicable source, applies quadratic
distance falloff, and injects RGB into the six directional lobes. A solid voxel wall
therefore blocks the source while a real opening passes it. Sources default to the two
nearest cascades, avoiding needless work in distant levels; both capacity and each
source's last cascade are configurable.

Registration, removal, movement, enable/disable, color, intensity, and range changes
invalidate only the union of the old/new influence bounds and only the permitted
cascades. The clipmap then reconverges through its existing tile budgets. The inspector
and cascade annotation report active emissives, revision, changes, dirty work, and the
buffer's estimated GPU memory.

### TestGI Phase-7 neon laboratory

Run **Tools > Dynamic GI > Phase 7 > Configure TestGI Emissive Injection**. It adds a
magenta neon panel and two probe markers to the east-window room. The green marker has
line of sight to the panel; the red marker is behind a voxelized divider. The selected
debug direction becomes `-Z`, while query, ground, and ceiling numeric slices remain
visible. `Phase7EmissiveTestGuide` labels the source, markers, and DDA occluder.

The validation disables the Sun to isolate the result, verifies visible/blocked
separation, disables the contributor, changes it from magenta to cyan, confirms the GPU
upload, and checks that invalidation covers fewer than all clipmap tiles:

```powershell
unity run . -- -force-d3d12 -executeMethod DynamicGI.Editor.RadiancePhase7SceneSetup.ConfigureTestGI -logFile -
unity run . -- -force-d3d12 -executeMethod DynamicGI.Editor.RadiancePhase7SceneSetup.ValidateTestGI -logFile -
```

This first version deliberately treats a source as isotropic and uses its renderer AABB
as a conservative source radius. It injects emitted light into probes but does not yet
propagate that energy between probes or apply it to scene materials automatically.

## Data layout

- Bricks are sparse CPU metadata mapped to fixed GPU slots.
- Brick resolution is normalized to a power of two between 4 and 32, allowing hot
  GPU occupancy lookups to use bit shifts instead of integer division.
- Each voxel uses one bit in the base occupancy buffer and one bit in the dynamic
  overlay buffer. A 16³ brick therefore consumes 512 bytes per layer.
- An open-addressed page table maps a brick coordinate to a GPU slot. The shader
  include `Shaders/GeometryField.hlsl` exposes `SampleGeometryOccupancy(positionWS)`.
- Only triangle surfaces are stored. Solid interiors are not filled, intentionally.
- Regional invalidation clears and reconstructs only touched bricks. This makes
  removals exact without requiring boolean subtraction in the overlay.

## GPU occupancy query

`WorldGeometryField.RequestOccupancy(positionWS, callback)` dispatches the query kernel
and returns through `AsyncGPUReadback`. `GeometryFieldDebug` can run this query at its
camera and show the result in the Scene view label.

For Shader Graph, use a File-mode Custom Function node pointing to
`Assets/DynamicGI/Shaders/GeometryField.hlsl`, function name
`SampleGeometryOccupancy`, precision `Float`, with a `Vector3 PositionWS` input and
`Float Occupancy` output.

## Current limitations

- MeshRenderer + MeshFilter triangle meshes are supported. Skinned meshes and Terrain
  require dedicated adapters in a later iteration.
- Position vertex attributes must be three-component Float32 data. This covers the
  conventional meshes currently used by the lab scene while keeping voxelization on
  the GPU and avoiding `Mesh.isReadable` requirements.
- Cutout/transparent material classification, normals, albedo, and emissive data are
  not stored yet.
- Bricks are not reclaimed after geometry is removed. Their contents become empty and
  correct, while the slot remains reserved until `Rebuild All`. This avoids page-table
  churn during rapid edits.
- The debug voxel list is capped and spatially subsampled when the active field exceeds
  that cap. This affects visualization only, never occupancy data.
- Sky directions currently use the world-up hemisphere rather than a surface-normal
  hemisphere. This makes Phase 3 an ambient/sky field, not a complete AO solution.
- The sky volume is fixed to the Geometry Field bounds. Radiance cascades may extend
  outside it, where sky accessibility uses its open-sky fallback.
- Phase 3 stores no radiance or color. Phase 4 stores source radiance but performs no
  neighbor propagation or diffuse bounce yet.
- Phase 5 reuses injected probes spatially but does not yet temporally blend new values;
  temporal accumulation is Phase 9 work.
- During a Sun revision, tiles can temporarily contain different accepted Sun states
  until their configured budgets converge. Phase 9 temporal accumulation will smooth
  this transition.
- Phase 7 emissive sources are isotropic AABB approximations. Textured/angular emission,
  spot cones, source-area integration, and GPU spatial binning are future refinements.
- Emissive injection is capped by `Maximum Emissive Contributors` (64 by default); the
  manager warns and ignores overflow instead of allocating during a frame.

## Mod/runtime registration contract

A future mod loader only needs to instantiate geometry and attach/configure
`GIGeometryContributor`; no probe placement is required. For a replacement:

1. Keep or capture the old bounds.
2. Disable/remove the old contributor and instantiate the new one.
3. Registration events invalidate the union of affected brick regions.
4. The field reconstructs those bricks within the configured per-frame budget.

For a neon sign or other emissive, attach `GIEmissiveContributor` to the renderer root
and call `Configure(renderers, color, intensity, range, maximumCascade)`. Disabling or
destroying that component removes its contribution and locally invalidates the field.
The contract intentionally remains independent of HDRP material internals so a mod
loader can derive these few values from its own asset metadata.
