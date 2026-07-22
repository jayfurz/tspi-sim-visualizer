# ICD — NN Guidance Package ⇄ tspi-sim

Interface Control Document for the neural-network guidance package. Three interfaces:
training data out (IF-1), model file in (IF-3), and the runtime call between them (IF-2).
Everything here is implemented and test-locked unless marked **OPEN**; §10 lists the
open items SME review should resolve.

| | |
|---|---|
| Parties | **Sim package** (this repo: engine, CLI, readers) / **Model package** (NN team: training code, delivered weights) |
| Authority | This document + the referenced schemas. Code and doc must not disagree; a disagreement is a defect. |
| Revision | 1 — 2026-07-16, initial issue for SME review |

## 1. Applicable documents

- `docs/CONVENTIONS.md` — frames, quaternion, time, units, determinism (normative for §2)
- `docs/FORMAT.md` — the `.tspi` container IF-1 is derived from
- `schemas/scenario.v1.schema.json` — scenario manifest (dispersions, guidance binding)
- Implementations: `tools/tspi_py/tspi_py/engagements.py` (IF-1),
  `src/Tspi.Sim/Engine/Guidance.cs` (IF-2), `src/Tspi.Sim/Models/GuidancePolicy.cs` (IF-3)

## 2. Global conventions (apply to every interface)

| Item | Convention |
|---|---|
| Position frame | NED (North-East-Down), right-handed, meters, from a per-run WGS-84 origin (lat°, lon°, ellipsoidal alt m) |
| Attitude | Unit quaternion, **w-x-y-z order**, Hamilton convention, body→NED |
| Body frame | x forward, y right, z down |
| Time | float64 **seconds since the run epoch**; epoch itself is int64 UTC nanoseconds |
| Units | SI throughout: m, m/s, m/s², rad/s. Degrees only in geodetic lat/lon |
| Numeric types | Stated per field below; f32 fields must not be silently promoted to f64 in shipments |
| Sample grid | One fixed `dt` per run (default 0.01 s); all series are gap-free on that grid |

## 3. IF-1 — Training data: `tspi-engagement/1`

One record per **launch event**. The record is a *view* rebuilt from `.tspi` run files
on load (`tspi_py.engagements(paths)`); it is serialized to a file only as a shipment
to a consumer without repo/run access, and then as **one file per batch**, never per
engagement or per run.

### 3.1 Record layout

`meta` — traceability:

| Field | Type | Description |
|---|---|---|
| `source` | string | producing `.tspi` path |
| `source_sha256` | 64-hex | manifest hash of the producing run |
| `origin_lla` | f64[3] | lat°, lon°, alt m — anchors the NED frame |
| `epoch_unix_ns` | int64 | UTC of t = 0 |

`launch` — snapshot at the launch instant (munition birth record; target sampled at the
same grid time):

| Field | Type | Units | Description |
|---|---|---|---|
| `t_s` | f64 | s | launch time |
| `munition_id` / `launcher_id` / `target_id` | string | | entity ids; `launcher_id` null if unparented |
| `pos_ned_m` | f64[3] | m | munition position |
| `vel_ned_mps` | f64[3] | m/s | munition velocity |
| `att_wxyz` | f64[4] | | munition attitude (body→NED) |
| `target_pos_ned_m` | f64[3] | m | target position at `t_s` |
| `target_vel_ned_mps` | f64[3] | m/s | target velocity at `t_s` |

`target` — the target track **windowed to the fly-out**: from launch to the munition's
terminal event, capped at launch + `window_s` (view parameter, default **100 s** — sized
to cover any stock model's `max_flight_time_s`). `window_s=None` yields the full
recorded track. Slices are zero-copy either way:

| Field | Type | Units | Description |
|---|---|---|---|
| `dt_s`, `t0_s` | f64 | s | grid period and first-sample time |
| `t_s` | f64[N] | s | materialized times (= t0 + i·dt) |
| `pos_ned_m` | f64[N×3] | m | |
| `vel_ned_mps` | f32[N×3] | m/s | |
| `att_wxyz` | f32[N×4] | | optional for consumers |
| `omega_body_rps` | f32[N×3] | rad/s | optional for consumers |

`outcome` — labels:

| Field | Type | Description |
|---|---|---|
| `terminal` | string∣null | `intercept` \| `ground_impact` \| `expire` \| `cpa` (precedence in that order); null if the munition's flight has no terminal event |
| `t_terminal_s` | f64 | NaN when `terminal` is null |
| `miss_m` | f64 | sub-dt refined closest approach; NaN when unavailable |

### 3.2 Exchange mechanisms

| Mode | Mechanism |
|---|---|
| In-repo (preferred) | `tspi_py.engagements([paths...])` → in-memory records, target arrays zero-copy over mmap. MATLAB equivalent `tspi_engagements.m` — **OPEN-1** (planned, not yet implemented) |
| Shipment | single `.mat` (`save_mat`; loads as `S.engagements{k}.launch.…`, cell array of structs) or JSON with identical field names. Shipments are regenerable artifacts, never edited in place |

### 3.3 Data-quality notes for training use

- Runs without a `dispersions` block in the scenario produce near-identical engagements
  across seeds. Training sweeps must disperse initial conditions (schema:
  `entities[].dispersions`) — see **OPEN-2**.
- Scenario `duration_s` defaults to 100 s and must cover the last launch plus its
  munition's `max_flight_time_s`; the manifest validator warns when a timed launch's
  fly-out would be truncated by the scenario window (truncated fly-outs yield `expire`
  outcomes that are artifacts of the window, not the engagement).
- Target tracks are **simulation truth** (or measured truth for imported runs, tagged in
  provenance `dynamics: measured+…`). No sensor model is applied — see **OPEN-5**.

### 3.4 Launch-frame variant: `tspi-dcv/1` — the fly-out struct

One element per engagement, in launch-centred **downrange / crossrange / vertical**
coordinates. This section describes the struct **as it appears in the workspace** —
file internals don't matter here; you open runs, you get `flyouts`, you plot.
(Separate versioned id per §6; `tspi-engagement/1` is unchanged. Training may consume
either view; the runtime observation contract stays `los_v1`, §4.)

#### What you get

`flyouts` is a 1×K struct array — one element per **launch event**, ordered by input
file, then by launch order within each run (appended engagements simply show up at
the end next time the run is opened). For engagement `k` (MATLAB 1-based):

```matlab
flyouts(k)
├─ .meta                          % traceability — where this engagement came from
│    .source                      % producing run file (char)
│    .source_sha256               % hash pinning the exact run (char)
│    .origin_lla        [1×3]     % lat°, lon°, alt m of the scene origin
│    .epoch_unix_ns               % UTC of t = 0
├─ .launch
│    .t_s                         % launch time, seconds since epoch
│    .munition_id  .launcher_id  .target_id      % entity names (char)
├─ .frame                         % the DCV frame, fixed at the launch instant
│    .origin_ned_m      [1×3]     % launch point (all DCV positions are relative to it)
│    .dcv_from_ned      [3×3]     % rows = D/C/V unit vectors; p_dcv = R*(p_ned - origin)
│    .downrange_ref               % 'target' (normal) | 'launch_velocity' (target overhead)
├─ .munition                      % the fly-out itself — only this view carries it
│    .t_s               [1×N]     % sample times, fixed dt (.dt_s, .t0_s also present)
│    .pos_dcv_m         [N×3]     % columns: downrange, crossrange, vertical (up), meters
│    .vel_dcv_mps       [N×3]     % same axes, m/s
│    .att_wxyz          [N×4]     % attitude quaternion, body→NED (NOT DCV — see OPEN-12)
│    .omega_body_rps    [N×3]     % body rates, body frame (NOT DCV — see OPEN-12)
├─ .target                        % same fields as .munition, same time window
└─ .outcome
     .terminal                    % 'intercept' | 'ground_impact' | 'expire' | 'cpa' | none
     .t_terminal_s  .miss_m       % when, and refined closest approach (NaN if n/a)
```

Both tracks are windowed to the fly-out: launch → terminal event, capped at
launch + 100 s (view parameter). Typical use reads exactly like it looks:

```matlab
m = flyouts(k).munition;
plot(m.pos_dcv_m(:,1), m.pos_dcv_m(:,3));   % height-above-launch vs downrange
xlabel('downrange m'); ylabel('vertical m');
```

#### The frame, in words

Origin at the munition's launch point. **Downrange** points along the horizontal
line from launch toward where the target was at launch (if the target is directly
overhead, the launch velocity heading is used instead — `frame.downrange_ref` says
which). **Crossrange** is positive to the right of that line, seen from above.
**Vertical** is height above the launch point, positive up. It is a plotting/feature
frame, not a dynamics frame (as a triad it is left-handed); recover scene NED any
time via `p_ned = origin_ned_m + dcv_from_ned' * p_dcv`.

#### How you get it

- **In-repo:** `tspi_py.dcv.dcv_flyouts([runs...])` rebuilds the structs in memory
  straight from the `.tspi` runs — nothing to export or keep in sync.
- **Shipment (MATLAB):** `tspi_py.dcv.save_mat('flyouts.mat', flys)`, then
  `F = load('flyouts.mat'); F.flyouts(k)...` — one file per batch, regenerable at
  will, never edited in place. (Native MATLAB reader: **OPEN-1**.)

Positions/velocities are stored as double; velocity precision is inherited from the
f32-recorded source and must not be presented as better than that.

#### Adding fly-outs after the fact

New trajectories enter as engagements, and the view picks them up automatically:

- **Flown by the sim** (NN policy or any guidance): `tspi append run.tspi
  addendum.json` launches new munitions against the already-recorded tracks — the
  original bytes never change, the new launch becomes engagement K+1 on the next
  `dcv_flyouts()` call, and the policy weights that flew it are hash-pinned in
  provenance.
- **Produced outside the sim** (measured or externally computed tracks):
  `tspi import data.csv` promotes them to a first-class run; munitions can then be
  appended against them and every engagement views in DCV like any other.

There is no hand-editing of a DCV struct back into a run — the run files stay the
single source of truth, and DCV is always derived from them.

## 4. IF-2 — Runtime inference: observation contract `los_v1`

In-process call from the sim to the loaded model, once per sim step `dt`, output held
(ZOH) across RK4 substages. **Stateless**: no history, no recurrence, no side channels.

### 4.1 Engagement geometry (computed by the sim, from truth states)

With `r = target.pos − self.pos`, `v_rel = target.vel − self.vel`, `range = |r|`:

- `closing = −(r · v_rel) / range`
- `ω = (r × v_rel) / range²` (LOS rate vector), `|ω|` its magnitude
- LOS frame: `e1 = r̂`; `e2 = ω̂`, or when `|ω| ≤ 1e-9` a **deterministic perpendicular**
  of `r̂` (cross with the coordinate axis least aligned with `r̂`, normalized);
  `e3 = e1 × e2`

### 4.2 Input — `obs`, float64[4], fixed order, dimensionless

| Index | Value | Normalizer (from the policy's own `norm` block) |
|---|---|---|
| 0 | range | `norm.range_m` |
| 1 | closing speed | `norm.speed_mps` |
| 2 | own (munition) speed | `norm.speed_mps` |
| 3 | \|ω\| | `norm.omega_rps` |

### 4.3 Output — float64[3], dimensionless

Commanded acceleration along `e1,e2,e3`; the sim computes
`a_NED = (a₀·e1 + a₁·e2 + a₂·e3) × norm.accel_mps2`.

### 4.4 Guards and limits (sim-side, outside the model)

| Condition | Behavior |
|---|---|
| `range ≤ 1e-3 m` | model not called; zero command (ballistic) that step |
| Airframe limit | commanded accel clamped to the munition model's `g_limit_max` (default 9 g) **after** the call — the model may command anything |
| Reference law | pronav in this space is `a_e3 = −N·closing·|ω|`; an analytic law, a distilled surrogate, and a trained policy are interchangeable behind this same call |

## 5. IF-3 — Model delivery: `tspi-policy/1`

One UTF-8 JSON file, placed in a model search directory and referenced from the
scenario (`munitions[].guidance: { "kind": "nn", "policy": "<name>" }`).

| Field | Type | Constraint |
|---|---|---|
| `schema` | string | exactly `"tspi-policy/1"` |
| `kind` | string | `"mlp"` |
| `obs` | string | `"los_v1"` (names §4 in full — layout, normalizers, guards) |
| `norm` | object | `range_m`, `speed_mps`, `omega_rps`, `accel_mps2` — all > 0 |
| `layers` | array | ordered; each `{ "w": f64[out][in], "b": f64[out], "act": "tanh"\|"relu"\|"linear" }` |

Validation enforced at load: rectangular `w`; `|b| = out`; layer widths chain;
first layer `in = 4`; last layer `out = 3`. Inference is hand-rolled float64 in the
sim — no ML runtime dependency; the identical forward pass is ~10 lines in any language.

**Traceability:** the SHA-256 of the policy file is recorded in every run's provenance
(`models` map) — a `.tspi` is always attributable to the exact weights that flew.

## 6. Versioning & change control

1. Named shapes are **frozen**: `tspi-engagement/1`, `tspi-dcv/1`, `los_v1`,
   `tspi-policy/1` never change layout, meaning, or units.
2. Needs beyond a frozen shape → a **new versioned id** (`los_v2`, `tspi-engagement/2`);
   producers/consumers negotiate by name. JSON containers may gain keys additively;
   consumers must ignore unknown keys.
3. Sim package owns: frames, units, event semantics, IF-1 production, IF-2 computation.
   Model package owns: `norm` values, network architecture within IF-3, training data
   selection.

## 7. Verification

- IF-1: pytest in `tools/tspi_py/tests` (golden `.tspi` committed; cross-language
  contract with the C# writer and JS reader). `tspi-dcv/1` frame/round-trip/windowing
  is pinned by `test_dcv.py` against the same golden file.
- IF-2/IF-3: `GuidanceTests` byte-locks pronav per-stage and the MLP forward pass;
  the determinism contract (`manifest + models + seed + sim_version → byte-identical
  .tspi`) pins any delivered policy's behavior platform-wide.
- Recommended on delivery: model package supplies ≥1 obs→output vector pair per
  delivered policy as a fixture; sim package adds it to `GuidanceTests`.

## 8. OPEN items for SME review

| # | Item | Current state | Question for SMEs |
|---|---|---|---|
| OPEN-1 | MATLAB reader (`tspi_engagements.m`) | planned, py/JS exist | needed before first delivery? |
| OPEN-2 | Dispersion spec for training sweeps | schema supports; example scenario has none | what IC spread & seed counts constitute an adequate training set? |
| OPEN-3 | Launcher trajectory in IF-1 | only launcher **id** + munition launch state | does the model condition on launcher motion pre-launch? |
| OPEN-4 | Environment exposure | wind/atmosphere affect truth but are absent from IF-1/IF-2 | should engagement records carry the run's environment block? should obs? |
| OPEN-5 | Sensor/seeker realism | `los_v1` is computed from **truth** states — no noise, latency, FOV, or track loss | acceptable for this phase, or is a measurement-model contract (`los_v2`+noise spec) required now? |
| OPEN-6 | Obs sufficiency | 4 scalars, stateless | target-maneuver history, time-to-go, off-boresight — needed? (any addition = new `obs` id) |
| OPEN-7 | Outcome labels | terminal kind + `miss_m` | richer endgame labels (hit aspect, closing speed at CPA, aimpoint error components)? |
| OPEN-8 | Multi-target / retargeting | one target per munition, fixed at launch | in scope for this interface generation? |
| OPEN-9 | Shipment format | `.mat` (cell-of-structs) + JSON | is `.mat` v5 acceptable, or is HDF5 (v7.3 / parquet) required by their toolchain? |
| OPEN-10 | Target RCS | not modeled | proposal: optional `rcs` in `tspi-model/1` as a **class** (`small`\|`medium`\|`large`) — classes keep this repo's notional-data rule — carried additively into the `.tspi` footer entity entry and exposed in the IF-1 target block; numeric/aspect-dependent signatures would belong to the future sensor contract (OPEN-5). Are classes sufficient, and what class ↔ platform mapping do SMEs want? |
| OPEN-11 | Track uncertainty | IC dispersions only (diagonal NED sigmas at t=0); no recorded or time-varying covariance | proposal: full 3×3 covariances as 6-element upper triangles (the rotated ellipsoid), per sample via the format's reserved layout evolution (layout 2 = +pos cov, stride 96; layout 3 = +pos & vel cov, stride 120); IF-1 target block gains `pos_cov [N×6]` additively. Truth records never carry fabricated covariance — it attaches to measured (`tspi import`) or synthesized-degraded tracks, where an authored piecewise covariance timeline (maneuver-style segments) would drive the degradation and could feed the model as an input alongside OPEN-5. Which of the three do SMEs need: fuller IC dispersion, recorded covariance, or the authored timeline? |
| OPEN-12 | Rotational data in DCV | `tspi-dcv/1` carries attitude body→NED and body rates in the body frame; only positions/velocities are expressed in DCV (§3.4 — DCV is a left-handed plotting triple, so quaternions were deliberately not re-expressed) | do SMEs/the NN need attitude and/or body rates in a DCV-aligned convention (e.g. Euler angles about D/C/V, or a right-handed DCV variant frame for rotational states)? If yes, that ships as a new versioned view (`tspi-dcv/2`) with the convention SMEs specify |
