# Godot viewer (Godot 4.4, GDScript-only)

Playback-only Godot client for `.tspi` trajectory files. **No simulation runs here** —
the headless `tspi` CLI produces files; this project renders them. Like the web viewer
it is fully source-auditable with zero dependencies beyond the engine itself: no Mono/C#,
no addons, no asset store — the format is parsed by a hand-rolled GDScript reader.
Godot is open source (MIT), so this is the path for environments where Unity
(proprietary, activation-bound) can't be approved but a native desktop app is wanted.

## Use

```sh
godot --path godot/TspiViewer                 # then drop a .tspi on the window, or Open…
godot --path godot/TspiViewer -- run.tspi     # or pass a path (absolute, or relative to the project dir)
```

- **Transport**: space play/pause, ←/→ ±1 s (shift ±10 s), Home/End, scrub bar with
  event tick marks, 0.25–8× rate, loop.
- **Scene**: team-colored entity darts oriented by recorded attitude, dim full path +
  bright flown-so-far trail, altitude poles, auto-sized ground grid, north/east axes.
  Left-drag orbits, right-drag/shift-drag pans, wheel zooms, `F` frames everything.
- **Panels**: file metadata (dt, origin LLA, `dynamics` honesty tag), entity list
  (click to follow; rows dim outside the entity's alive window), footer event log
  (click to seek).

Entities appear at their `t0` and vanish at their last sample, exactly as recorded.
Every pose on screen is an interpolated sample from the file (Hermite position with
stored-velocity tangents, slerped attitude — identical math to
`TspiReader.TrySampleAt`); scrubbing anywhere is O(1) per entity.

## Frame

Godot is right-handed y-up with −Z forward — the same render frame the web viewer
uses: `godot = (E, −D, −N)`, so +X = east, +Y = up, −Z = north, and the mapping is a
proper rotation (no handedness flip). Attitude converts by rotating the body axes
through the recorded quaternion into NED, mapping them into Godot space, and
rebuilding an orthonormal basis (`scripts/ned_godot.gd`) — the `NedUnity.cs` approach.

## Layout

- `scripts/tspi_file.gd` — the format reader (header, CRC-checked footer via the EOF
  trailer, entity table validation, O(1) interpolated sampling). Ports
  `web/viewer/tspi.js` / `src/Tspi.Core/Runtime/IO/TspiReader.cs`; keep the three in
  lockstep. All math in GDScript floats (64-bit), so f64 positions survive until the
  final cast to render-space `Vector3` (f32).
- `scripts/playback_controller.gd` — one node per entity, trails, ground grid,
  play/seek/loop.
- `scripts/orbit_camera.gd`, `scripts/playback_hud.gd`, `scripts/main.gd` — camera,
  UI (built in code; the `.tscn` is five nodes), bootstrap.
- `tests/test_reader.gd` — cross-language contract test against the committed golden
  file (same assertions as `tools/tspi_py/tests/test_reader.py`, plus frame-mapping
  and interpolation checks). Runs headless, no display needed:

```sh
godot --headless --path godot/TspiViewer --script tests/test_reader.gd
# smoke test: 60 frames of the real scene against the golden file
godot --headless --path godot/TspiViewer --quit-after 60 -- ../../tools/tspi_py/tests/data/golden-v1.tspi
```

## Limits (deliberate, for now)

- Playback only — no scenario edit loop yet. The Unity editor's edit→run→resume loop
  needs the `tspi` CLI; the same `OS.execute` pattern would work here (Godot can run
  the CLI and reload with `keep_time`), it just isn't built.
- No terrain/imagery. The georeference is in every file header (`origin_lla`); when
  terrain matters, Godot has community Cesium/terrain options, but the CesiumJS web
  path is further along.
- Reads layout-1 records (and wider forward-compatible strides), same as every other
  reader in the repo.
