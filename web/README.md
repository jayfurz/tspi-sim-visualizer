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
- **Scenario editor** (served mode only): when the page is hosted by `tspi serve`, an
  `edit` panel appears — manifest JSON in a textarea, validate/run buttons, optional
  seed override, and "resume at t": the new run reloads at the current playback time.
- **Deep links** (http only): `index.html?file=<url>&t=<sec>` fetches a served file
  and opens paused at `t`; `?scenario=<url>` preloads the editor with a manifest.

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
tspi serve schemas/examples/intercept.json   # prints /?scenario=… — the editor deep link
curl -X POST --data-binary @scenario.json localhost:8080/api/validate
curl -X POST --data-binary @scenario.json localhost:8080/api/run?seed=7
#   -> {file: "/files/runs/serve/…tspi", viewer: "/?file=…", events: […], …}
```

`GET /files/<path>` serves `.tspi` runs and `.json` scenarios under the serve root
(`--root`, default cwd) read-only; everything else 404s. Runs land in `--out-dir`
(default `runs/serve/`). The in-page editor (or a curl loop) POSTs the edited manifest
to `/api/run` and reloads the result at the same playback time — determinism gives the
Unity-style resume: everything before the edit replays identically. Binds `127.0.0.1`
only unless `--bind` says otherwise. Integration tests: `src/Tspi.Tests/ServeTests.cs`;
exercised in `scripts/e2e.sh`.

## Limits (deliberate, for now)

- The editor is a JSON textarea, not drag-gizmos — Unity's `ScenarioEditController`
  remains the graphical authoring surface; this loop is for quick parameter/seed
  iteration anywhere a browser reaches the CLI.
- No terrain/imagery. The georeference is in every file header (`origin_lla`); when
  terrain matters, CesiumJS (Apache-2.0, self-hostable) is the path that mirrors the
  Cesium-for-Unity plan in `unity/README.md`.
- Reads layout-1 records (and wider forward-compatible strides), same as every other
  reader in the repo.
