# tspi-sim — aircraft & munitions TSPI simulator + Unity visualizer

Headless, deterministic flight/munition simulation that writes memory-mapped `.tspi`
trajectory files from human-readable JSON scenario manifests, plus a Unity 6 playback
client and a numpy reader. Millions of TSPI samples, O(1) random access, O(new-samples)
appends, byte-identical reruns.

**All vehicle models are notional.** This repo carries no real performance data.

## Quickstart

```bash
# .NET 10 SDK required (https://dot.net). Unity 6000.0.x for the viewer (optional).
cd src && dotnet test && cd ..                       # 75 tests: format, recovery, V&V, import, golden lock
alias tspi="dotnet $PWD/src/artifacts/bin/Tspi.Cli/Debug/net10.0/tspi.dll"

tspi validate schemas/examples/intercept.json
tspi run schemas/examples/intercept.json             # -> schemas/examples/runs/intercept-0042.tspi
tspi inspect schemas/examples/runs/intercept-0042.tspi --events --provenance

# later munitions vs the recorded run (footer-chained append, old bytes untouched)
tspi append schemas/examples/runs/intercept-0042.tspi schemas/examples/addendum-late-munition.json

# measured TSPI (e.g. range data, CSV) imports to a first-class .tspi — simulated
# munitions then fly against the measured tracks via append, never re-simulating them
tspi export schemas/examples/runs/intercept-0042.tspi -o /tmp/measured.csv  # stand-in for real data
tspi import /tmp/measured.csv --origin 34.9061,-117.8839,700

# Monte Carlo across all cores + campaign index
tspi sweep schemas/examples/intercept.json --seeds 1:200 -j 10 --out-dir /tmp/sweep
python3 -c "import json;print(sum(1 for l in open('/tmp/sweep/index.jsonl')))"

# ...or fan a campaign across a cluster instead of one box:
tspi sweep schemas/examples/intercept.json --seeds 1:10000 --emit slurm --out-dir /data/camp > run.sbatch

# analysis (numpy, zero-copy mmap)
pip install -e "tools/tspi_py[test]"
python3 -c "
from tspi_py import TspiFile
f = TspiFile('schemas/examples/runs/intercept-0042.tspi')
print(f, f.events[0].kind, f.samples('blue-01')['pos'][-1])"
```

End-to-end check of everything above: `scripts/e2e.sh`.

## Unity viewer (Unity 6000.0.x)

Open `unity/TspiViewer` with Unity Hub. The shared format library arrives as local UPM
package `com.tspi.core` (from `src/Tspi.Core` — same code the sim wrote the file with).
Create an empty scene, add `TspiPlaybackController` (+ `PlaybackHud`) to a GameObject,
point `filePath` at a `.tspi`, press Play: per-entity objects with team-colored trails,
scrubbing, pause, 0.25–16× time dilation. The viewer **never simulates** — it interpolates
recorded samples (Hermite position, slerped attitude), so scrubbing a million-sample file
is O(1) per entity. See `unity/README.md` for georeferencing (Cesium) notes.

## Documentation

- `docs/FORMAT.md` — the `.tspi` container, normative (header/records/footer/trailer,
  append & recovery, layout-evolution rules)
- `docs/CONVENTIONS.md` — frames, quaternion, time, units, determinism (read first)
- `docs/ARCHITECTURE.md` — component map, fidelity level, scaling numbers
- `schemas/` — JSON Schemas for manifests/models + validated examples

## Design invariants

1. Simulation is headless & deterministic (fixed-step RK4, f64, per-entity RNG streams);
   `manifest + models + seed + sim_version → byte-identical .tspi` on one platform
   (golden-locked in CI; cross-platform is tolerance-based via `tspi diff --tol-m`). Runs
   stream to disk as they integrate, so a sweep never buffers whole trajectories.
2. Unity is a pure playback client through the same `Tspi.Core` reader.
3. Appends never rewrite bytes: torn appends recover (`tspi recover`, fuzz-tested at every
   truncation offset), live readers are safe, the footer chain keeps every historical index
   snapshot, and the persisted environment lets appended munitions fly in the original air mass.
4. Manifest evolution is additive (channel-based maneuvers, discriminated unions); record
   evolution is by `layout` id extending a fixed 64-byte prefix (old readers skip unknown
   layouts). Fidelity is kinematic 3-DoF + synthesized attitude, tagged in provenance — not
   aero-moment 6-DoF.
