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
