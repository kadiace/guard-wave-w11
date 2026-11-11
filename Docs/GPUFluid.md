# Forest GPU FLIP/APIC wave

## Scope

- Unity Editor **6000.3.28f1**, URP, GPU Compute Shader required.
- The authored Forest river is 24 world units wide (`x = -12..12`).
- The upper river is `z = 50..-15`; wave material enters from the positive-Z end and travels toward negative Z.
- At the cliff lip (`z = -15`), moving water leaves the 3D pressure domain and falls under gravity until the authored lower creek surface (approximately 30 m below).
- Prepared Forest scene objects (HUD, static upper/lower river water, waterfall, defense flag) are **not** recreated at runtime.
- The "Stage Flood Water" scene object is disabled; the new flood is rendered from GPU particle positions.

## Computational model

`Assets/Resources/GuardWaveFlip.compute` provides:

1. `Spawn`: allocate a finite pulse of 3D particles at the upstream boundary.
2. `ParticleToGrid`: trilinear mass-weighted velocity transfer with a per-particle affine term (APIC); fixed-point atomic accumulators for GPU portability.
3. `NormalizeGrid`: normalize velocities and apply gravity.
4. `JacobiPressure`: iterative pressure solve on a collocated 3D Eulerian grid (a **prototype**, not a staggered MAC discretization).
5. `ProjectGrid`: subtract the pressure gradient, with solid velocity clamps.
6. `PressureToBodies`: sum bounded pressure impulses on exposed Rigidbody surfaces.
7. `GridToParticle`: blend PIC and FLIP updates (FLIP fraction 0.92), calculate new affine terms, advect particles, resolve collisions.
8. `Waterfall`: inexpensive ballistic motion once particles cross the -Z cliff lip.

Collision shapes are approximated by analytic boxes, spheres, capsules, and oriented local-bounds boxes for MeshColliders. AABB-only pressure/swept forces from the old CPU flood system are removed. Complex concave mesh SDFs are **not implemented**; the proxy geometry is only a coarse approximation.

`GuardWaveFlipFluid.cs` provides GPU resource ownership and sends bounded aggregate impulses/torques to Unity Rigidbody components using `AsyncGPUReadback`. This introduces asynchronous latency. Static walls/floor are represented by grid boundaries, dynamic solids by GPU-uploaded collider proxies. The pressure model is approximate; it is **not** a validated incompressible CFD solver.

`GuardWaveFlipRenderer.cs` renders GPU particle discs directly through URP without per-particle GameObjects or CPU particle readback. These billboards are not a continuous reconstructed liquid surface (Marching Cubes and high-quality screen-space water are future options).

## Performance budget

| Parameter | Initial budget |
| --- | --- |
| Upper river X width | 24 m fixed |
| Grid spacing | 0.6 m |
| Grid | Approximately 42 × 16 × 112 (~75k cells) |
| Maximum particles | 24,000 |
| Pressure iterations | 22 per physics tick |
| Maximum Rigidbody handles | 384 |
| Maximum active GPU collider proxies | 256 |
| Source emission interval | 0.12 s |
| Fixed step | Uses Unity Time.fixedDeltaTime |
| GPU -> CPU readback | Compact impulses per Rigidbody, one readback in flight |

The bounds and emission rate are deliberately limited. Thin sticks and narrow gaps smaller than ~0.6 m are under-resolved; rotated meshes use OBB proxies rather than full triangle collision. On slower GPUs lower particle count/pressure iterations or introduce collision spatial bins. No platform-specific performance measurements have been obtained.

## Play Mode acceptance checklist (requires Unity Editor)

1. Open `Assets/Scenes/Forest.unity` in Unity 6000.3.28f1.
2. Confirm the Console has **no** C# errors and the compute and URP water shader import.
3. Press Play, place a rubber duck/large stone, then click Ready.
4. Check water enters near `z = 50` with `x` in `[-12,12]` and travels toward -Z.
5. Observe water particles diverted by collider shapes and the duck/stone physically moving or rotating.
6. Confirm the forest sticks and defense flag respond to forces, and stationary river objects remain authored scene content.
7. Observe particles falling below the cliff lip after reaching `z = -15`.
8. On flag failure, confirm the popup appears once, Confirm rolls back to the pre-Ready positions, and the next wave can start.
9. Restart repeatedly; check Profiler for GPU usage and leaked ComputeBuffers.
10. Test URP transparent sorting and water Outline compatibility in the current project camera.
11. Compare on target GPU and adjust cell/particle budgets based on measured frame times.

## Limitations / unresolved validation

The current agent environment cannot launch the Unity Editor or test ComputeShader compilation on the target GPU. Repository-level static checks cannot substitute for Play Mode, visual inspection, or Profiler results. The physics implements a limited-grid FLIP/APIC approximation, not a production-ready high-fidelity water solver. In particular, grid pressure is collocated (not MAC), pressure coupling is approximate, the mesh collision geometry is simplified, and visual rendering consists of particle billboards.

The game preserves the authored Forest scene and its ready/failure workflow. All modifications are restricted to the new wave implementation and its integration.

## Ambient creek baseline: feature/real-wave

The authored upper creek bed is five meters deeper. Two box-collider soil walls
run along the former shore edges and join the existing bank mesh. The cliff face
is shorter at the top but still meets the untouched lower creek floor, and the
static waterfall surface matches the new lip. The defence flag and Beaver House
shift down with the creek to preserve their relative placement.

`GuardWaveGame.ambientFlowPreview` defaults to `true` for this validation.
When enabled, the game immediately fills the upper river with **one meter of
real FLIP particles**, steps the simulation continuously in `FixedUpdate`,
and keeps the original Ready button enabled once the water is initialized.
At scene load it **does not create a wave**; a single crest is emitted only
when the player presses Ready. The old standalone finite Ready-stage pulse
remains in code for non-ambient scenes.

The GPU uses a full-length initial particle lattice. A particle crossing the
cliff into ballistic spray increments an exit counter. A GPU recycling kernel
replaces inactive upstream particles for recorded outflow, preserving the
flowing water rather than indefinitely allocating particles.

**Inflow is now velocity-proportional:** each FixedUpdate calculates the
maximum refill particles per second from the base-water particle density
and configured Ambient Flow Speed:

`refill particles / sec = initial 1m-water particles / river length * ambient velocity`

A fractional accumulator avoids rounding drift. The GPU only refills up to
this per-tick rate **and** the number of ordinary creek particles already
lost at the cliff; excess lost-water demand carries into later ticks. This
maintains the baseline 1m volume rather than creating extra water when the
wave travels through the river. At the default 0.6m grid and 24m × 65m
river, the nominal supply is about 368 particles/s at 2m/s or 3673
particles/s at 20m/s. The actual amount is bounded by available dead
GPU particle slots and downstream losses. The one-shot crest particles
are excluded from background refill accounting. A separate `Ambient Flow Speed`
(default 2m/s) is used instead of the flood inlet speed (currently 30–60m/s).
The ambient-only impact multiplier defaults to 0.01, so the stored 100x
wave-force tuning does not violently eject sticks during calm-flow testing.
Fresh particles enter in the upstream ghost cells outside the filled river,
rather than overlapping existing water in the last populated row.
The static Upper River Water is shown in the editor but its renderer is
disabled at runtime so SSFR can show the actual fluid.

**Limitations:** This balances individual particles, not measured real-world
volume flux. The recycled particle budget requires inactive slots while
waterfall spray is alive. A coarse 0.6m FLIP grid, the prototype pressure
projection, and GPU–CPU collider force latency can create visible fluctuations.
Validate 1m apparent depth, erosion/accumulation, shoreline boundaries and
recycling over sustained gameplay in Unity Play Mode.

### One-shot crest triggered by Ready

In ambient mode, Ready adds **one 3m amplitude** crest above the **existing 1m**
creek: the target crest depth is therefore **4m**. The crest is seeded only
once per click with a sech-squared longitudinal shape in the upstream section.
A wave-only marker in particle velocity.w prevents this temporary extra mass
from being permanently replaced by the normal creek inlet. The creek keeps
moving before, during and after the wave; the wave solver is not recreated
or disposed when the wave succeeds.

Tunable in `GuardWaveFluidSettings.asset`:
- Wave Crest Amplitude: 3m additional height (1m base + 3m = 4m target)
- Wave Crest Speed: 8m/s initial velocity at peak
- Wave Crest Width: 2.5m sech-squared characteristic width
- Wave Crest Length: 14m upstream seeded region

The persistent background inlet is separately driven at the configured
Ambient Flow Speed. Cliff exits from ordinary water accumulate in a GPU
replenishment counter that persists until inactive slots are available, and
transient wave exits do not raise the baseline over time. In a failure popup,
the same GPU water keeps moving; confirming failure restores the saved Rigidbody
positions and reconstructs the 1m background water without re-emitting a crest.
A successful wave ends after its estimated traversal time, not by disposing
the background water.

The 4m value is the *seeded target shape*, not a guaranteed measured surface
height after FLIP pressure projection, collision and motion. The single-shot
crest requires spare particle slots and may need solver/particle budget tuning.
Unity Editor Play Mode and GPU profiling have not been run here. The Unity editor and GPU
profiler are not available from this environment.

## Screen-space surface rendering

The URP renderers now include `GuardWaveSSFRFeature`, which replaces discrete
particle discs with continuous screen-space fluid rendering. The feature draws the
existing GPU particle buffer to half-resolution front-depth and thickness buffers,
performs two edge-preserving bilateral depth blur passes, then reconstructs normals
from the smoothed eye depth and composites water before transparent objects.
Opaque scene depth prevents water from covering terrain and props in front.

Adjust `Assets/Resources/GuardWaveFluidSettings.asset` in the Inspector. The
settings contain the physical impact multipliers, inlet bounds, solver budgets
and SSFR resolution, blur, thickness, water color, refraction and opacity.
Solver allocation/physics changes apply to the *next* wave; appearance values
are sampled for rendering while the current wave is visible.

**Known limitations:** screen-space water does not generate a real 3D surface or
physical collision mesh, has screen-edge and multiple overlapping liquid-layer
artifacts, and depends on the URP camera depth and opaque color textures.
Validate the Game camera, Scene view, transparencies and quality variants in
Unity 6000.3.28f1. Editor shader compilation and GPU frame timing have not
been performed in this environment.

## Open downstream outlet at the cliff (feature/real-wave)

The original FLIP grid stopped within a single cell of the physical
`z = -15m` cliff lip. Its general `IsSolid` condition treated the
negative-Z boundary as a wall; `ProjectGrid` zeroed negative-Z
velocities and the pressure solver used a solid Neumann condition.
This could make water accumulate at the cliff rather than fall.

The downstream grid now extends **four cells past the lip** (2.4m at
0.6m grid spacing) while retaining two upstream ghost cells.
`IsOpenOutflow` marks only the downstream face as open; other
out-of-range faces remain solid. `PressureAt` uses zero gauge pressure
outside that outlet and `ProjectGrid` no longer clamps negative-Z
velocity there. The bed-solid mask ends at the actual lip, not at the
far downstream buffer boundary. GPU particles crossing `z < -15m`
immediately switch to ballistic falling water, with a first-tick
vertical gravity kick and no double XZ advance.

For diagnosis, the ambient GPU solver counts actual particles
crossing the lip independently of the refill debt. The following
`GuardWaveFlipFluid` properties are updated through a single
async GPU readback approximately once per second (no per-frame
logging):
`TotalCliffExits`, `TotalAmbientRefills`,
`PendingAmbientRefills`. They can be inspected in a debugger.
At sustained flow, the first two should grow; the pending count
should not climb without bound once sufficient inactive slots exist.

The creek's 1m initial depth, 20m/s ambient velocity, 3m additional
crest, 80m/s crest speed, existing water rendering and refill
rate scaling remain unchanged. Unity Editor Play Mode and GPU
behavior still require hands-on validation.

## Beaver House activation and inactive scene pieces

Authored Beaver House sticks can now be inactive before Play and enabled or
hidden later without causing `GuardWaveGame.RegisterScenePieces` to pass a null
Rigidbody into its snapshot. `GuardWavePiece.Body` uses lazy component
resolution because Unity may not have invoked `Awake` on an inactive parent.
The renderer cache is also initialized on demand.

Initial restart snapshots and pre-wave rollback store each stick's own
`activeSelf` and the activation of its parent hierarchy. Scene Rigidbody
snapshots include inactive objects, and the active/selectable scene-piece
membership is preserved across rollback without reviving pieces removed
before Ready. Fluid collider discovery includes inactive scene Rigidbodies,
while the per-step collider upload still filters by `activeInHierarchy`.
Thus turning Beaver House on or off during Play automatically enables or
disables its collider contributions in the same running FLIP simulation.

Play Mode tests to run in Unity: start with Beaver House inactive; toggle it
on/off after flow begins; press Ready with it inactive; toggle it during a
wave; fail and confirm rollback; restart; ensure the parent activation state,
outline selection and fluid collisions match the intended state. Unity Play
Mode and GPU execution were not available for this change.
 
## Increased background water supply (2x)

The `feature/real-wave` baseline now uses
`Ambient Flow Volume Multiplier = 2` in
`Assets/Resources/Datas/GuardWaveFluidSettings.asset`.
Unlike simply doubling the per-tick `supplyBudget`, which is capped by
recorded cliff losses, this doubles the actual number of *initial*
water particles per meter of river. The one-meter-deep initial region
is seeded with twice the longitudinal density, so flowing water and
the number of ordinary particles crossing the cliff both increase.
The existing velocity-proportional refill calculation uses this
initial population and therefore automatically scales the replenishment
budget by 2x, without counting it twice.

With the current 24m wide, 65m long river and 0.6m grid the baseline
particle count rises from about 11,938 to about 23,876; its nominal
20m/s replenishment rate rises from ~3,673 to ~7,347 particles/s.
`MaxParticles` increases from 24,000 to 48,000 to leave spare slots
for the 3m crest and falling spray. `Ambient Water Depth` remains
**1m**, `Wave Crest Amplitude` remains **3m**, and `Wave Crest Speed`
remains **80m/s**. This is a particle-density/volume representation
increase; the prototype solver does not guarantee that the rendered
water surface will be uniformly higher or that every part of the
channel will have uninterrupted coverage.

In Unity Play Mode, check that the riverbed is consistently covered,
the `TotalCliffExits` and `TotalAmbientRefills` rise continuously,
and FPS/VRAM usage remain acceptable with the larger GPU buffer.
No Unity runtime tests were available from this environment.

## Cohesive 3m crest at constant ambient speed (pressure solver unchanged)

Normal flow remains **10m/s** (in the serialized fluid settings) before,
during and after Ready. Ready emits one additional connected 3m water crest
whose particles start at the same 10m/s as the existing river; no velocity
boost is applied to the background river. The extra volume is filled at
the same longitudinal particle density as the background river and has
smooth end ramps. The existing loss-based upstream refill rate remains
proportional to the configured ambient speed. Failure confirmation
rebuilds the same 10m/s creek.

The experimental face-based pressure solver, obstacle-wake damping,
pressure-iteration increase and adaptive substeps from commit 312116a
are intentionally **not included**. The former JacobiPressure,
ProjectGrid, fluid single-Step() and 22 pressure iterations are retained
for comparison against the earlier river behavior. Changes in this
branch do not guarantee that 3m height stays perfectly coherent under
unmodified FLIP physics; visual/runtime validation is still needed.

## 3m crest formation and diagnostic fix (feature/real-wave)

The previous one-shot `EmitCrest` atomically claimed **whatever inactive
particle slots happened to exist**. Spray and background refill could consume
the available slots before Ready. The GPU dispatch checked the nominal
48,000 capacity against the initial population but did not validate the
real candidate spawn count at click time. As a result a crest could be
missing large portions before any physics integration.

The ambient creek now reserves a **deterministic, contiguous 3m crest
lattice** at the end of the GPU particle buffer. The background refill
kernel is not allowed to borrow those slots, even before Ready. With the
currently configured 0.6m cell size / 2x longitudinal density the
15,510 possible crest candidate slots remain protected; roughly 12,784
of them represent the smooth-tapered 3m volume. MaxParticles rises to
64,000 so the water below and spray can continue without starving the
reserved crest. The one-shot emission writes the entire reserve
deterministically (inactive for tapered-off cells), avoiding a GPU
atomic lottery. No new water is supplied to the crest on later ticks.

A separate deformable height-support term is applied **only to extra
crest particles**, with a damped vertical spring towards each parcel's
original height above the 1m surface and gentle downstream velocity
alignment. This compensates for gravity and compression by the existing
coarse FLIP solver while the wave travels across unimpeded water.
The parcel's original relative height is carried in unused APIC
`affineX.w` and its sticky collision-release flag in `affineY.w`.
When a particle reaches within the configured `Crest Release Distance`
of a Rigidbody collider it permanently loses the height support and
returns to ordinary FLIP physics, including gravity and collision
response. The cliff exit also disables the support and continues the
existing falling-spray simulation. This is a **gameplay-oriented shape
stabilizer**, not a physically derived solitary-wave solution.

For diagnosis, the GPU now reports `ActiveCrestParticles`,
`ReleasedCrestParticles` and `HighestCrestDepth` through asynchronous
readback about every 0.5 seconds; these are inspectable on
`GuardWaveFlipFluid` without adding logs or changing the Game camera.
HighestCrestDepth is the maximum particle-center depth above the
bed (not a reconstructed continuous water-surface measurement).

The ambient speed remains **10m/s before, during and after Ready**,
initial creek depth remains **1m**, added crest amplitude remains
**3m**, the old pressure solver and single-fixed-step update are
unchanged. The crest remains a one-time finite water volume. Tests
in Unity Play Mode should compare high-water visibility and impacts,
including empty channel vs. blocked channel, and confirm the
crest particle diagnostics do not suddenly drop at creation.

**No Unity Editor compilation or GPU execution testing was possible here.**

## Narrower one-shot crest (current values)

The crest remains full width across the **24m river**. Its thickness
along the direction of flow is reduced from **14m to 8m**, and its
front/back ramp width is reduced proportionally from **2.5m to 1.4m**.
This keeps a similarly tapered profile rather than making the central
plateau wider through a ramp-width-only change. The crest slot reserve
is recalculated from the configured length when the fluid initializes;
at 0.6m cell size and 2x longitudinal density it now reserves
**8,742 candidate slots** instead of 15,510. The one-time **3m extra
height**, **1m ambient depth**, **10m/s constant speed**, collision
release, and existing pressure solver are unchanged.
