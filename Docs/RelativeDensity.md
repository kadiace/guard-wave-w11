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

At every fixed step, `GuardWaveFlip.compute` reconstructs water columns from
live particles after the FLIP update. The `RelativeDensityToBodies` kernel
samples a 5x5x5 volume for each active collider; cylinder and sphere shapes
discard outside samples. The submergence fraction `s` gives buoyant
acceleration `|g| * s / relativeDensity` upward, against Unity's normal
gravity downward. Forces/torques are delivered through the existing
Rigidbody impulse buffer.

Local current following uses `fluidVelocity - rigidbodyPointVelocity`.
The flow does **not** rely on the collider surface normal; hence a log follows
water both sideways and along its own axis. Static-ground contacts on sinking
objects reduce the **target current** to 15%, not just the acceleration.
The old collision/pressure impulses remain active.

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
