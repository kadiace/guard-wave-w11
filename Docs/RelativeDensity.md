# Relative-density fluid response (Forest)

The FLIP/APIC creek remains the single source of real fluid movement. Rigidbody
response uses particle-derived XZ surface-height/velocity columns; it does not
teleport objects or impose a fixed downstream velocity.

## Per-object control

Edit **GuardWavePiece > Fluid Interaction > Relative Fluid Density** in the
Inspector. The number is **object density / stage-fluid density**, not an
absolute force:

- `0.25`: floats with roughly 25% submerged volume.
- `0.60`: typical log; roughly 60% submerged.
- `1.00`: neutrally buoyant when completely submerged.
- `2.60`: sinks even when completely submerged.

The component defaults to `0.6` so existing authored Beaver House sticks float
without modifying the 860KB Forest scene. Every object prefab has its own
explicit starting value; these are gameplay tuning values, not measured
material densities. **Edit values freely for future stages.**

### Runtime-spawned objects

Forest creates its ambient water solver before the first palette placement.
`GuardWaveGame.Spawn()` must call `_fluid?.RegisterPiece(piece)` after
instantiating a prefab. The solver registers the new Rigidbody and its
non-trigger colliders; otherwise GPU fluid collision and buoyancy kernels
never see palette-spawned items even when their density Inspector field
is correct (e.g. rubber duck = 0.25). Live palette objects are prioritized
over inactive authored colliders when limited by the GPU collider budget.

At every fixed step, `GuardWaveFlip.compute` reconstructs water columns from
live particles after the FLIP update. The `RelativeDensityToBodies` kernel
first samples the waterline/current at eight locations JUST OUTSIDE the
collider footprint, then integrates a 5x5x5 volume INSIDE the collider against
that external surface; cylinder and sphere shapes discard outside samples.
Sampling water presence INSIDE the collider is incorrect: particles are
displaced by the collider so occupied interior fluid cells disappear. The submergence fraction `s` gives buoyant
acceleration `|g| * s / relativeDensity` upward, against Unity's normal
gravity downward. Forces/torques are delivered through the existing
Rigidbody impulse buffer.

Local current following uses `fluidVelocity - rigidbodyPointVelocity`.
The flow does **not** rely on the collider surface normal; hence a log follows
water both sideways and along its own axis. Static-ground contacts on sinking
objects reduce the **target current** to 15%, not just the acceleration.
Raw particle and pressure reaction impulses remain active for non-piece
bodies (e.g. the defense flag), while relative-density pieces instead receive
coherent volumetric buoyancy and flow drag. The particles still collide with
every piece and are deflected around the obstacle.

## Global tuning

`Assets/Resources/Datas/GuardWaveFluidSettings.asset`:

- `_waterFlowResponse = 6`: horizontal current-following rate (per second).
- `_verticalFlowResponseScale = 0.35`: fraction of current drag along Y;
  buoyancy and gravity dominate vertical movement.
- `_groundedFlowMultiplier = 0.15`: target horizontal-current fraction for
  heavy, statically grounded objects.

If an object is out of the real water volume, no buoyancy/current force is
generated. Waterfall particles are excluded from the horizontal supported
waterline, so buoyancy does not linger below the cliff.

## Verification in Unity Play Mode

1. Place one log in a calm, filled creek. It should rise and remain partially
   submerged without a target-Y teleport.
2. Rotate the log 90 degrees and compare flow along its axis and across it;
   both orientations should move downstream.
3. Compare one `0.25`, `1.0`, and `2.6` object with similar size/shape:
   floating, neutral, and sinking respectively.
4. Place a dense object on static riverbed. It should have reduced current
   following but remain movable by strong wave impacts.
5. Observe a log moving toward the waterfall. It should lose buoyancy when
   supported water ends and then fall naturally.
6. Activate Beaver House and watch per-stick forces, collisions, and fps.

## Floating-object stability

For EVERY body with a relative fluid density field (including heavy rocks),
raw particle collision impulses and per-cell pressure reaction impulses are
deliberately NOT delivered to Rigidbody. These previously added unbounded
positive-Y impacts to dense rocks. Fluid particles still collide against the
object surface and respond to obstacles inside FLIP. The Rigidbody instead
receives distributed buoyancy/current forces from the surrounding water.
Non-piece bodies retain their original direct impact behavior.

Critically, sampling water presence at the *interior* of a solid collider
makes an immersed object seem dry: the FLIP particles have been pushed aside.
The solver now probes eight sites outside the object's projected XZ footprint,
then calculates submerged body volume against the surrounding free surface.
This allows a rubber duck (density 0.25) to emerge from the creek bed and follow
the flow. Floating current convergence is doubled but its per-step impulses
remain capped.

The `SmoothWaterColumns` kernel blends the highest water particle across
neighboring columns, then smooths successive frames (about 8/s), without
replacing the original physically simulated particle field. Volume samples
softly transition across the waterline. Buoyancy force on a light object is
capped to 1.5 times its weight while fully immersed, and additional vertical
velocity damping reduces bobbing. Buoyancy and current torques are reduced
to 12%, with Rigidbody angular damping of at least 5. Very light box-shaped
objects additionally tend to return upright. These are shared settings,
**not extra per-object parameters**.

Tuning parameters in `GuardWaveFluidSettings.asset`:
`_floatingVerticalDamping`, `_floatingTorqueScale`,
`_floatingMaxBuoyancyRatio`, `_floatingUprightStrength`,
`_floatingAngularDamping`.

Check the rubber duck at relative density 0.25: it should bob briefly and
settle with most of its body above the water while moving downstream. Rotate
it sideways before play and check that it returns roughly upright. Check
that a large wave still carries the duck rather than shoving it one particle
at a time; verify a heavy rock rests on the bed and only moves when the
distributed flow is strong enough (it should not jump due to microscopic
particle/pressure kicks).

## Approximation / caveats

The free surface is reconstructed as the highest particle in a 0.6m XZ
column plus a small particle radius, bilinearly sampled. This is deliberately
game-oriented, not a watertight CFD interface; stray spray or thin columns can
produce noisy waterlines. The sampled shapes are box, cylinder, or sphere
approximations rather than voxel-perfect mesh volumes. GPU->CPU impulse
readback remains asynchronous, with up to eight snapshots in flight and
possible drops under a sustained GPU backlog.

**Unity runtime / GPU kernel compilation and playtesting have not been run
from the GitHub connector.** Verify the scene in Editor before merging.
