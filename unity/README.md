# TspiViewer (Unity 6000.0.x)

Playback-only Unity client for `.tspi` trajectory files. **No simulation runs here** —
the headless `tspi` CLI produces files; this project renders them.

## Open & run

1. Unity Hub → Add → `unity/TspiViewer` (created against **6000.0.34f1**; any 6000.0.x works).
2. The `com.tspi.core` package resolves from `src/Tspi.Core` via a relative `file:`
   reference — the *same sources* the simulator used to write the file, so the format
   can never drift between producer and viewer.
3. Generate the walkthrough run from the repo root (docs/WALKTHROUGH.md):
   `tspi run schemas/examples/ship-to-air.json -o runs/ship-to-air.tspi`.
4. Open **`Assets/Scenes/Playback.unity`** and press Play. The scene ships with
   `TspiPlaybackController` + `PlaybackHud` pre-wired and `filePath` set to
   `runs/ship-to-air.tspi` — relative paths resolve against the repo root, and if the
   file is missing the HUD says so and shows the command instead of a blank screen.
   (For any other run, set `filePath` on the `TspiViewer` object: absolute, or
   repo-root-relative.)

You get: one object per entity (team-colored trails: blue/red/gray), a dim full-recorded
path per entity so the whole engagement geometry is visible at any playback time (parity
with the web/Godot viewers; toggle `showFullPaths`, decimated to `maxPathPoints`),
spawn/despawn on launch/intercept, pause/scrub/loop and 0.25–16× time dilation from the
HUD, and event ticks (launch/CPA/intercept) on the scrub bar. Scrubbing is O(1) per
entity — pos/vel Hermite + attitude slerp over the mmap, courtesy of
`TspiReader.TrySampleAt`. The controller also exposes the open `TspiReader` (`Reader`),
whose `ReadSample(entity, i)` is an O(1) mmap read — overlay/analytics scripts can walk
every sample of every entity, not just the current playback pose.

## Scenario editing (edit → run → scrub)

Add **ScenarioEditController** to the same GameObject and set:

- `scenarioPath` — a scenario manifest (absolute, or relative to the Unity project
  root), e.g. `../../schemas/examples/intercept.json`. **Saved in place on regenerate.**
- `tspiExecutable` / `tspiDllPath` — a self-contained `tspi` binary, or `dotnet` plus
  the absolute path to `src/artifacts/bin/Tspi.Cli/Debug/net8.0/tspi.dll`.
- `workingDirectory` — the repo root, so `./models` resolves (or set `modelsDir`).

Controls: **Tab** toggles edit mode. In edit mode, click an entity marker to select,
**left-drag** moves it (altitude preserved), **right-drag** points its initial heading
at the cursor; the side panel edits speed/heading/altitude, cycles team, adds and
deletes entities. In either mode, the maneuver buttons add a segment for the selected
entity **at the current playback time** (snapped to the dt grid), and the panel lists
and deletes existing segments.

Every edit saves the manifest and re-runs the real CLI (`tspi run` validates first;
~100 ms for a 70 s engagement), then reloads the preview **at the time you were
watching**. The determinism contract makes that seamless: maneuvers are command
timelines and RNG streams are per-entity, so everything before the edit time replays
byte-identically — locked by the `ManeuverAtT_LeavesPrefixByteIdentical…` test in
`ScenarioDocumentTests`. Regenerating feels like branching the world from the current
moment. Unity still never simulates: the preview on screen is always the sim's
own output. Preview files alternate under `Application.temporaryCachePath` so the
mmap'd file currently playing is never overwritten.

The manifest tree itself is the edit document (`ScenarioDocument` in `Tspi.Core`, via
MiniJson) — fields the editor doesn't know about survive round-trips, and semantic
errors surface from the CLI's validator in the status line. Desktop editor/player only
(child-process spawn is unavailable on mobile/web).

## Notes & known limits

- **If Unity can't be approved or binaries can't be delivered**: `web/viewer/` is a
  playback-only viewer with the same never-simulates contract — dependency-free
  JS/WebGL that runs from source in any browser. See `web/README.md`.
- **Not yet compiled in CI**: this repo's CI builds the .NET side; the Unity project is
  scaffolded (correct package layout, asmdefs, 6000.0 pin) but was authored without a
  Unity editor in the loop. First open may surface small API nits — expected cost ≈ minutes.
- `.meta` files are intentionally not committed for the scaffold; Unity generates them on
  first open. Commit them from then on (standard Unity hygiene).
- Scene origin sits at the NED origin; float32 is fine to ~60 km offsets. For real-world
  terrain + imagery under the trajectories, add **Cesium for Unity** and parent the
  playback root to a `CesiumGeoreference` at the file's `origin_lla` (header fields) —
  that also solves float precision at long ranges via origin rebasing.
- Input uses the legacy `Input`/IMGUI path so the sample works in any fresh project;
  swap for the Input System + UI Toolkit in a production viewer.
