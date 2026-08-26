# Architecture

```
scenario.json ──► tspi validate ──► tspi run / sweep (headless, deterministic)
   (manifest)                             │
models/*.json ────────────────────────────┤  fixed-step RK4, f64, seeded RNG streams
measured.csv ──► tspi import ─────────────┤  measured tracks resampled to the fixed dt grid
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
   from stored pos+vel, slerped attitude). The scenario *editor* keeps this contract:
   it authors manifest JSON (`ScenarioDocument`, shared via `Tspi.Core`) and shells out
   to the tspi CLI for every preview, so the trajectory on screen is always the real
   sim's output — and because edits are command-timeline edits, the run before the edit
   time replays byte-identically, which is what makes "regenerate and resume at the
   same t" seamless.
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
   Measured TSPI enters through the same seam: `tspi import` converts externally
   recorded tracks (CSV; regular or irregular rate) into a `.tspi`, and an addendum
   flies simulated munitions against them — entities that were measured are never
   re-simulated. Imported files are stamped `op: "import"` with a `measured+…` dynamics
   tag (FORMAT.md) so nobody mistakes range data for sim output or vice versa.

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
- Munitions: the entire fly-out generator sits behind the
  **`IMunitionTrajectoryModel` seam** (`src/Tspi.Sim/Engine/IMunitionTrajectoryModel.cs`)
  with a deliberate one-model-per-file rule: the stock generator is
  `PointMassMunitionModel.cs`, and swapping in a different one (6-DoF, an external
  NN fly-out producer, a HIL proxy) means adding a sibling file that implements the
  interface and pointing `MunitionTrajectoryModels.Default` at it — the engine,
  writers, and viewers never change. The stock model: point-mass with boost thrust
  along velocity, quadratic drag against the air
  mass (exp-atmosphere), gravity, and a guidance law behind the inner **`IGuidanceLaw`
  seam** (`src/Tspi.Sim/Engine/Guidance.cs`): true proportional navigation by default
  (evaluated at every RK4 stage — byte-locked by the golden test), or a learned policy
  (`guidance.kind: "nn"`) — a hand-rolled f64 MLP over the versioned LOS-frame
  observation `los_v1`, evaluated once per output sample and zero-order-held across the
  RK4 step so commands change only on the dt grid. The airframe g-limit clamps
  **outside** the law, so no policy — analytic or learned — can exceed the envelope.
  Policy weights (`tspi-policy/1`) resolve like vehicle models and their SHA-256 joins
  the provenance `models` map; train/distill in Python on sweep output, export weights,
  fly them here with no ML runtime in the loop. Attitude aligned to velocity. All model
  parameters are **notional** — keep real performance data out of this repo.
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
models/               notional vehicle models + guidance policies (sha-256'd into file provenance)
src/Tspi.Core/        shared format+math+manifest-authoring library == Unity package com.tspi.core
                      (Runtime/Live/ = live-stream consumer shared by .NET and Unity)
src/Tspi.Sim/         manifest parsing, engine (autopilot, rigid-body rotation, guidance
                      seam pronav/nn, wind, RK4), measured-TSPI importer (CSV -> fixed dt)
src/Tspi.Cli/         tspi verb CLI (validate/run/sweep/append/import/inspect/recover/export/
                      diff/serve/record)
src/Tspi.Tests/       xUnit: format round-trip, recovery, analytic V&V, golden lock
tools/tspi_py/        numpy mmap reader + pytest against the same golden file
tools/live-stream/    live WebSocket feed into web/viewer: wire contract (PROTOCOL.md),
                      header-only C++/Boost.Beast producer, .tspi replay producer
unity/TspiViewer/     Unity 6000.0.x playback (file or live stream) + scenario-editing
                      client (never simulates;
                      previews shell out to the tspi CLI)
scripts/              e2e.sh, check_schemas.py
```

## Live sources (streaming producers)

The viewers consume files; a *running* simulator can also push state into the web
viewer **or the Unity viewer** over a WebSocket (`tools/live-stream/`, wire contract in
its `PROTOCOL.md`).
The stream carries the container's own 64-byte layout-1 records with time implicit
(`t0_ns + i*dt_ns`) — no second serialization format, and no second interpolator: the
JS reader exposes a `LiveTspiFile` with the same surface as the file reader, and on the
.NET side `TspiReader` and `LiveTspiSource` are both an `ITspiSource` sharing one
interpolator (`TspiSampling`) — so a live pose and the replayed pose of the same run
are bit-identical (asserted by `web/viewer/tests/live.test.mjs` and `LiveSourceTests`). This keeps principle 1 intact for streams: the
producer is authoritative for dynamics, the viewer only interpolates and draws.
An external simulator that streams is not required to write `.tspi` at all: `tspi
record <ws://…>` (`src/Tspi.Sim/Live/LiveRecorder.cs`) subscribes like any other
viewer and lands the run in the container, so a live engagement ends up replayable,
diffable and appendable-to without the producer knowing anything about the file
format. Recording copies the streamed records rather than re-interpolating them, so a
recorded replay is bit-identical to its source; imperfect input (dropped frames,
mid-stream joins, non-sign-continuous quaternions) is repaired to the container's
rules and *counted in provenance* rather than silently smoothed.

## Scaling numbers (measured on the 12-core dev box)

- 70 s, 3-entity engagement at 100 Hz: ~100 ms wall, ~700× real time, 1.1 MiB file.
- 200-seed Monte Carlo sweep: 1.7 s on 10 workers (~116 runs/s) → ~2k runs/s expected
  on the 198-core box. Record math: 64 B/sample → 30 entities × 100 Hz ≈ 11 MiB/min.
