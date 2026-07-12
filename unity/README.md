# TspiViewer (Unity 6000.0.x)

Playback-only Unity client for `.tspi` trajectory files. **No simulation runs here** —
the headless `tspi` CLI produces files; this project renders them.

## Open & run

1. Unity Hub → Add → `unity/TspiViewer` (created against **6000.0.34f1**; any 6000.0.x works).
2. The `com.tspi.core` package resolves from `src/Tspi.Core` via a relative `file:`
   reference — the *same sources* the simulator used to write the file, so the format
   can never drift between producer and viewer.
3. New empty scene → empty GameObject → add **TspiPlaybackController** and **PlaybackHud**.
4. Set `filePath` to an absolute path of a `.tspi` (e.g. the output of
   `tspi run schemas/examples/intercept.json`), press Play.

You get: one object per entity (team-colored trails: blue/red/gray), spawn/despawn on
launch/intercept, pause/scrub/loop and 0.25–16× time dilation from the HUD. Scrubbing is
O(1) per entity — pos/vel Hermite + attitude slerp over the mmap, courtesy of
`TspiReader.TrySampleAt`.

## Notes & known limits

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
