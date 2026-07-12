# Architecture

```
scenario.json ──► tspi validate ──► tspi run / sweep (headless, deterministic)
   (manifest)                             │
models/*.json ────────────────────────────┤  fixed-step RK4, f64, seeded RNG streams
                                          ▼
                                   runs/*.tspi  (mmap container, docs/FORMAT.md)
                                          │
             ┌────────────────────────────┼──────────────────────────────┐
             ▼                            ▼                              ▼
   Unity 6 TspiViewer            tools/tspi_py (numpy)           tspi append (later
   playback-only client          analysis / Arrow export         munitions vs recorded
   (no simulation in-engine)     on the HPC box                  tracks, O(new samples))
```

## Principles

1. **Unity never simulates.** The game loop is variable-dt, float32, and frame-coupled —
   wrong for deterministic TSPI. Unity consumes `.tspi` files through the same
   `Tspi.Core` reader the CLI uses, interpolating poses at render time (Hermite position
   from stored pos+vel, slerped attitude).
2. **The sim is a headless CLI.** Fixed timestep (RK4), double-precision state, one
   deterministic RNG stream per (seed, purpose, entity). This is what makes `tspi sweep`
   an embarrassingly-parallel Monte Carlo: runs/s scales linearly with cores; the
   198-core box is the target, not a GPU (branchy 6-DOF guidance is a CPU workload; GPUs
   only enter if you later batch thousands of uniform entities or train a policy).
3. **One shared format library.** `src/Tspi.Core` is simultaneously a netstandard2.1
   project and a Unity UPM package (`com.tspi.core`, `file:` referenced by the viewer).
   Zero package dependencies — the footer JSON codec is hand-rolled (`MiniJson`) so
   Unity needs no Newtonsoft/System.Text.Json. Manifest parsing (user input) uses
   System.Text.Json in `Tspi.Sim`, which Unity never references.
4. **Munitions fly against tracks, not entities.** Guidance consumes an `ITargetTrack`
   (Hermite-sampled trajectory). Live scenario and later `tspi append` use the same code
   path — the only difference is whether the track comes from memory or from the file.

## Fidelity level (deliberate, documented)

**Translation is kinematic 3-DoF; attitude fidelity is per-model.** The `.tspi` record
stores full 6-DoF-shaped state (quaternion + body rates). By default attitude is
*synthesized* from the flight path and rates are derived. Aircraft models that declare a
`rotational` block instead fly a **true rigid-body rotational integrator** behind the
same `IAircraftDynamics` seam (`src/Tspi.Sim/Engine/RigidBody.cs`): quaternion
kinematics q̇ = ½ q⊗ω and Euler's equations ω̇ = I⁻¹(τ − ω×Iω) on a principal-axis
inertia tensor, RK4 at the sim dt, driven by a torque-saturated quaternion PD controller
that tracks the autopilot's flight-path reference attitude. Attitude then lags and
rate-limits like an airframe, and recorded body rates are the integrated ω. Every
produced file is stamped with a `dynamics` provenance tag (`…+synth-attitude`,
`…+rigid-attitude`, or `…+mixed-attitude` — see FORMAT.md) so consumers know exactly
what the 6-DoF-shaped records represent. Full aero-moment 6-DoF (forces from attitude,
not just torques toward a reference) would need aero data that deliberately stays out of
this repo.

- Aircraft: **kinematic autopilot** — point-mass translation driven by three channel
  commands (lateral turn-to-heading with g-limit, vertical rate/altitude capture, speed
  hold/set). Default attitude: synthesized from flight path + coordinated-turn bank,
  body rates finite-differenced. With a model `rotational` block: attitude integrated
  from rigid-body EOM as above (notional inertia/torque authority, e.g.
  `models/generic-fighter-rb.json`). Lift implicitly balances gravity.
- Munitions: point-mass with boost thrust along velocity, quadratic drag against the air
  mass (exp-atmosphere), gravity, and true proportional navigation with a g-limit;
  attitude aligned to velocity. All model parameters are **notional** — keep real
  performance data out of this repo.
- Wind: constant vector or altitude-layered profile, plus first-order Gauss-Markov
  gusts (per-entity seeded streams). 4-D gridded weather is a future provider.
- Terrain: flat plane at origin altitude (ground-impact events). A DEM heightfield
  provider is the designed next step (see FORMAT/CONVENTIONS notes on geoid).

## Events & endgame

Guided munitions terminate at fuze radius (`intercept`) or at closest approach
(`cpa` + miss distance) once range starts opening; unguided ones fly to `ground_impact`
or `expire`. Closest approach is refined to **sub-dt precision** (a fine scan plus a
parabolic polish over the smoothly-interpolable missile and target tracks), because CPA
almost never lands on a sample boundary and the reported miss distance is the number the
whole campaign turns on. Events carry interpolable timestamps and entity ords; kill
*adjudication* (Pk) is deliberately out of scope — do it in Python over the recorded CPA data.

## Determinism scope

Same machine, same `manifest + models + seed + sim_version` → **byte-identical** output,
enforced by `DeterminismTests` (always on) and the golden byte-lock. Across *different*
platforms, floating-point and transcendental differences mean bit-exactness is **not**
guaranteed: the golden byte-lock is a reference-platform check (CI on Linux/x64), and
cross-platform reproducibility is tolerance-based via `tspi diff --tol-m`. Treat one
platform as canonical for a campaign, or compare with a tolerance. A large campaign that
must be bit-reproducible across heterogeneous nodes would need a fixed-precision math
path — out of scope for v1, and called out here rather than hidden.

## Repo map

```
docs/                 FORMAT.md (normative), CONVENTIONS.md, this file
schemas/              JSON Schemas + examples/ (validated in CI, golden.json locks format)
models/               notional vehicle models (sha-256'd into file provenance)
src/Tspi.Core/        shared format+math library == Unity package com.tspi.core
src/Tspi.Sim/         manifest parsing, engine (autopilot, rigid-body rotation, pronav, wind, RK4)
src/Tspi.Cli/         tspi verb CLI (validate/run/sweep/append/inspect/recover/export/diff)
src/Tspi.Tests/       xUnit: format round-trip, recovery, analytic V&V, golden lock
tools/tspi_py/        numpy mmap reader + pytest against the same golden file
unity/TspiViewer/     Unity 6000.0.x playback client (never simulates)
scripts/              e2e.sh, check_schemas.py
```

## Scaling numbers (measured on the 12-core dev box)

- 70 s, 3-entity engagement at 100 Hz: ~100 ms wall, ~700× real time, 1.1 MiB file.
- 200-seed Monte Carlo sweep: 1.7 s on 10 workers (~116 runs/s) → ~2k runs/s expected
  on the 198-core box. Record math: 64 B/sample → 30 entities × 100 Hz ≈ 11 MiB/min.
