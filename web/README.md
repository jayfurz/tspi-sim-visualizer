# Web viewer — playback-only, dependency-free

A browser-based `.tspi` playback viewer: `viewer/index.html` + two hand-rolled JS files.
No build step, no package manager, no external dependencies, no network access — it
parses the binary format with `DataView` and renders with raw WebGL1. Delivery is
source files; anywhere with a browser can run it, including air-gapped environments
where Unity (proprietary, activation-bound, not source-available) can't be approved.
Same contract as the Unity viewer: **it never simulates** — every pose on screen is an
interpolated sample from the file (Hermite position with stored-velocity tangents,
slerped attitude — identical math to `TspiReader.TrySampleAt`).

## Use

Open `web/viewer/index.html` — directly from disk (`file://`) or served by any static
server — and drag a `.tspi` onto it. Nothing is uploaded; the file is read in-page.

- **Transport**: space play/pause, ←/→ ±1 s (shift ±10 s), Home/End, scrub bar with
  event tick marks, 0.25–8× rate, loop.
- **Scene**: team-colored entity darts oriented by recorded attitude, dim full path +
  bright flown-so-far trail, altitude poles, auto-sized ground grid, north/east axes.
  Left-drag orbits, right-drag/shift-drag pans, wheel zooms, `F` frames everything.
- **Panels**: file metadata (dt, origin LLA, `dynamics` honesty tag), entity list
  (click to follow; rows dim outside the entity's alive window), footer event log
  (click to seek).
- **Deep links** (http only): `index.html?file=<url>&t=<sec>` fetches a served file
  and opens paused at `t` — the hook the future `tspi serve` edit loop will use.

Entities appear at their `t0` and vanish at their last sample, exactly as recorded —
a munition doesn't exist before `launch` and disappears at `intercept`/`ground_impact`.

## Layout

- `viewer/tspi.js` — the format reader (header, CRC-checked footer via the EOF
  trailer, entity table validation, O(1) interpolated sampling). Ports
  `src/Tspi.Core/Runtime/IO/TspiReader.cs`; keep the two in lockstep.
- `viewer/app.js` — WebGL renderer, orbit camera, playback state, DOM panels.
- `viewer/index.html` — markup + styles, loads the two scripts. Classic scripts (no
  modules) so `file://` works everywhere.
- `viewer/tests/` — Node cross-check of the JS reader against the trusted Python
  reader (`tools/tspi_py`) on real files:

```sh
python viewer/tests/ref_dump.py run.tspi ../tools/tspi_py/tests/data/golden-v1.tspi > ref.json
node viewer/tests/parser.test.mjs ref.json run.tspi ../tools/tspi_py/tests/data/golden-v1.tspi
```

## Limits (deliberate, for now)

- Playback only. The Unity-style scenario edit loop needs something that can run the
  CLI; the plan is a `tspi serve` verb on the existing .NET CLI serving this page plus
  run/validate endpoints, keeping the browser a pure UI shell.
- No terrain/imagery. The georeference is in every file header (`origin_lla`); when
  terrain matters, CesiumJS (Apache-2.0, self-hostable) is the path that mirrors the
  Cesium-for-Unity plan in `unity/README.md`.
- Reads layout-1 records (and wider forward-compatible strides), same as every other
  reader in the repo.
