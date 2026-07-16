# Conventions (pin-down list)

Every item here is a silent-corruption bug if two implementations disagree. They are
fixed for v1 and enforced in code + tests.

## Frames

- **NED** is a local tangent plane at the scene origin LLA: **x = north, y = east,
  z = down**, right-handed, meters. It is a flat-earth approximation; altitude error
  grows ~`d²/2R` (~78 m at 100 km, ~786 m at 316 km). Long-range scenarios should run in
  ECEF internally and treat NED as an I/O convenience.
- **ECEF / LLA** use **WGS84**. Altitudes are **ellipsoidal** (no geoid/orthometric
  correction). A DEM-based terrain provider would need geoid handling; noted for future.
  Measured data is frequently MSL: `tspi import --geoid-offset-m N` adds the local geoid
  undulation as a constant (valid over a test range) and records it in provenance —
  importing MSL altitudes without it silently offsets the file by the undulation.
- **Body frame**: x forward, y right, z down.
- **Unity** (viewer only): left-handed, y up. Mapping: `unity = (E, −D, N)`; +Z = north,
  +X = east, +Y = up. Conversion lives in one place (`NedUnity.cs`) with a round-trip test.

## Attitude

- Quaternion is **Hamilton convention, W-first (w,x,y,z)**, rotating **body→NED**:
  `v_ned = q ⊗ (0,v_body) ⊗ q*`.
- Euler angles are the **aerospace 3-2-1 sequence**: yaw (about NED down) → pitch (about
  intermediate right/east) → roll (about body forward). Used only at human boundaries
  (manifest `att_ypr_deg`, UI readouts).
- Stored quaternion samples are **sign-continuous**: `dot(q_i, q_i+1) ≥ 0`, so playback
  slerp never takes the long path. Enforced at write time.

## Time

- In-file time is **int64 nanoseconds** relative to `header.epoch_unix_ns`.
- Sample time is **implicit**: `t_i = t0_ns + i * dt_ns`. Never stored per-record.
- The sim never accumulates `t += dt` in floating point; it uses integer step indices.

## Units

Meters, m/s, m/s², radians internally (degrees only in manifests/UI), kilograms, newtons,
seconds. Wind layer direction is **meteorological** ("from" bearing, degrees true).

## Byte order

Little-endian throughout. Readers assert the host is little-endian (all current targets
are); a big-endian port would byte-swap at the I/O boundary only.

## Determinism

- One **RNG stream per (seed, purpose, entity-id)** (SplitMix64), so adding an entity
  never perturbs another entity's draws.
- Fixed-step RK4, f64 integrator state and stored positions.
- Maneuver commands activate on the dt sample grid, so RK4 never integrates across a
  mid-step command discontinuity (an off-grid `at_s` snaps to the next sample, with a
  validator warning).
- **Same-platform:** same manifest + models + seed + sim version → byte-identical output.
- **Cross-platform:** NOT bit-exact (floating-point/transcendental divergence). The golden
  byte-lock is a reference-platform check; use `tspi diff --tol-m` for portable comparison.
  Persisted environment (atmosphere + wind) travels in the footer so a later `tspi append`
  reproduces the same air mass rather than flying in still air.
