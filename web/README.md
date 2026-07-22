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
  and opens paused at `t` — the hook `tspi serve` uses (below).

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

## tspi serve — the edit loop backend

`tspi serve` (a verb on the .NET CLI, `src/Tspi.Cli/Commands/ServeCommand.cs`) hosts
this page over http and adds the endpoints the Unity edit loop gets by shelling out —
the browser stays a pure UI shell, the CLI stays the only thing that simulates:

```sh
tspi serve runs/intercept-0042.tspi        # serves the viewer, prints a deep-link URL
curl -X POST --data-binary @scenario.json localhost:8080/api/validate
curl -X POST --data-binary @scenario.json localhost:8080/api/run?seed=7
#   -> {file: "/files/runs/serve/…tspi", viewer: "/?file=…", events: […], …}
```

`GET /files/<path>.tspi` serves any run under the serve root (`--root`, default cwd)
read-only; everything else 404s. Runs land in `--out-dir` (default `runs/serve/`).
POST an edited manifest to `/api/run`, open the returned `viewer` URL with `&t=<sec>`,
and determinism gives the Unity-style resume: everything before the edit replays
identically. Binds `127.0.0.1` only unless `--bind` says otherwise. Integration tests:
`src/Tspi.Tests/ServeTests.cs`; exercised in `scripts/e2e.sh`.

## Limits (deliberate, for now)

- The edit UI itself isn't in the browser yet — `/api/run` + deep links make the loop
  scriptable (curl, editor tooling); an in-page manifest panel is the next increment.
- No terrain/imagery. The georeference is in every file header (`origin_lla`); when
  terrain matters, CesiumJS (Apache-2.0, self-hostable) is the path that mirrors the
  Cesium-for-Unity plan in `unity/README.md`.
- Reads layout-1 records (and wider forward-compatible strides), same as every other
  reader in the repo.
