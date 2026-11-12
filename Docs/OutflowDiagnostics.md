# Outflow diagnostic mode (Ctrl+G)

This commit adds only GPU instrumentation and a colored view of already simulated particles. It does not change fluid velocities, water supply, pressure conditions, collision response, or particles' lifetime.

1. Run the Forest scene and press **Ctrl+G** once. Press Ctrl+G again to turn diagnostics off. The same shortcut enables both the colored particle overlay and one aggregated Console log per second.
2. The overlay marks only *active FLIP particles* in **-8 <= x <= 8** and **ExitZ <= z <= ExitZ+6** (currently z=-15 to -9). Red means **velocity.z > +0.1m/s** (upstream/reverse), blue means **velocity.z < -0.1m/s** (downstream), and yellow means **near zero**. Waterfall spray (state=2) is intentionally excluded.
3. Console line `[GuardWave Outflow]` reports per-physics-step GPU counters, sampled once per second: fluid grid count, reverse grid velocities before and after `ProjectGrid`, newly reversed grid cells due to projection, peak positive grid velocities, active particle count and reversed counts before/after particle collisions (and crest support), newly reversed particles during collision/crest processing, peak positive particle velocity, and cliff exits for that sampled step.
4. Reproduce with the barrier, then without it. If `projectionNewReverse` rises where the backflow begins, focus on the pressure solve and lip/outflow boundary. If `collisionNewReverse` rises instead, inspect collider handling. If both remain low while the reverse count is high, inspect velocities inherited from previous steps and transport through the wake.

**Notes:** The reported numbers are an instantaneous sampled step, *not* a sum over the preceding second. The debug overlay uses the existing SSFR material's additional rendering pass and does not modify actual water colors or SSFR surfaces. GPU atomic counters are activated only when Ctrl+G is enabled. The diagnostics intentionally cover the central channel rather than the entire 24m width.


## Observed-volume outlet boundary coupling

The physical lip is **z=-15m**, between the last upstream cell
(`_LipCellZ=4`, cell center -14.7m) and the existing four-cell outlet
buffer (first center -15.3m). In the running ambient creek:

- The `GridToParticle` crossing transition increments a **per-x/y lip-face**
  counter in `_FlowCounters[4..]`, *in addition to* the original global
  water-loss counters. No extra G2P UAV is needed (the GPU has an 8-UAV cap).
- The representative particle volume is **24m × 1m × channel length /
  initial ambient particle count**. Therefore the exit-volume rate for one
  face is `count × volume / fixedDeltaTime`. Crest parcels have the same
  spatial sample density. The 1:2:1 lateral filter preserves the total face
  count, and a 0.12s exponential temporal filter avoids particle-lattice
  quantization causing large stepwise pressure spikes.
- `UpdateExitFlux` runs on the GPU **after** G2P and writes the filtered
  observed outlet face-normal speed. The following step's
  `JacobiPressure` substitutes that observed **outward face flux** into
  the divergence at wet cells touching the physical lip, instead of
  **adding** a second suction source on top of the existing free outlet.
  The normal zero-gauge pressure/free surface remains unchanged.
- When there is no measured exit flow, the existing pressure divergence is
  retained; nothing forcibly accelerates dry faces or reverses particles.
  A separate smoothing buffer makes this a one-step delayed measurement,
  avoiding CPU readback latency entirely.
- `Ctrl+G` logs now include `allCliffExitsThisStep` (whole width) and
  `filteredOutletQ` (estimated m³/s, sum of smoothed face speeds times cell
  face area). The existing `cliffExitsThisStep` diagnostic remains limited
  to the central ±8m strip.

This is an **incremental finite-volume flux boundary correction in the
existing collocated solver**, not a full conservative staggered MAC solve.
It does **not** artificially lower pressure by a fixed amount per parcel;
it does **not** force all water to move downstream. Before judging the
visual outcome, run Unity Play Mode/compute compilation on the target GPU,
then compare central reverse-flow fraction, lip exit rate, outlet Q, and
water levels with Ctrl+G before/after this commit.


## MAC face projection and connected cliff waterfall

The fluid no longer switches to ballistic mode at the instant it crosses
z=-15. The GPU liquid domain now extends **7.2m beyond the lip**, with a
**3m lower liquid region** below the upper creek bed. A parcel remains FLIP
liquid until it has traveled 6m beyond the lip or fallen 3m below the bed.
Only then is it switched to the existing ballistic `Waterfall` code.
Gravity is integrated once per timestep, with no artificial suction.

**Pressure solver:** APIC P2G still accumulates cell velocities, then
`BuildFaces` constructs staggered +X/+Y/+Z normal velocities. The pressure
Poisson divergence, solid/air boundary conditions and `ProjectFaces`
gradient use matching discrete face operators; `ProjectGrid` reconstructs
projected cell samples for the existing PIC/FLIP/G2P and the Ctrl+G overlay.
The old collocated central-difference projection and the previous
measured-exit-divergence override are removed. The Jacobi pressure solver
runs 36 iterations (formerly 22) on the larger connected domain.

**Accounting:** An otherwise-unused `affineZ.w` marker records each
particle's first physical lip crossing, so the existing exit counters
and the optional `filteredOutletQ` comparison measure real passages
without counting a particle every frame as it falls. Ambient refill debt
is issued only when the base-water parcel finally leaves the connected
liquid solver. `filteredOutletQ` is now independent instrumentation,
not a velocity or pressure constraint; the actual discharge is resolved
by projected face fluxes across the physical lip.

The same ambient 10m/s, crest speed/height, FLIP blend and collision
response are retained. This is a **MAC pressure stage**, not a complete
face-native APIC deposition; Unity ComputeShader/Play Mode validation and
GPU profiling are still required. Ctrl+G visual diagnostics are retained.
