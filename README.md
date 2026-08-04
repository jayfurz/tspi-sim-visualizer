# tspi-sim — aircraft & munitions TSPI simulator + Unity visualizer

Headless, deterministic flight/munition simulation that writes memory-mapped `.tspi`
trajectory files from human-readable JSON scenario manifests, plus a Unity 6 playback
client and a numpy reader. Millions of TSPI samples, O(1) random access, O(new-samples)
appends, byte-identical reruns.

**All vehicle models are notional.** This repo carries no real performance data.

## Quickstart

```bash
# .NET 8 SDK required (https://dot.net). Unity 6000.0.x for the viewer (optional).
cd src && dotnet test && cd ..                       # 96 tests: format, recovery, V&V, import, guidance, serve, golden lock
alias tspi="dotnet $PWD/src/artifacts/bin/Tspi.Cli/Debug/net8.0/tspi.dll"

tspi validate schemas/examples/intercept.json
tspi run schemas/examples/intercept.json             # -> schemas/examples/runs/intercept-0042.tspi
tspi run schemas/examples/ship-to-air.json           # ship-to-air reference engagement: VLS-style
                                                     # launch kick, SAM vs dispersed inbound
tspi run schemas/examples/nn-intercept.json          # learned (NN) guidance: hand-rolled f64 MLP,
                                                     # weights sha-256'd into provenance
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

# browser playback + edit loop backend: serves web/viewer plus run/validate endpoints
tspi serve schemas/examples/runs/intercept-0042.tspi   # prints a deep-link URL to open

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
is O(1) per entity.

Add **ScenarioEditController** to the same GameObject and the viewer becomes a scenario
editor: drag entities, retarget headings, add maneuvers *at the current playback time* —
each edit saves the manifest, re-runs the real `tspi` CLI (~100 ms), and resumes playback
at the same moment. Determinism means everything before the edit replays byte-identically,
so it feels like branching the world from "now". See `unity/README.md` for setup and
georeferencing (Cesium) notes.

## Web viewer (no Unity, no dependencies)

`web/viewer/index.html` is a playback-only viewer that runs in any browser straight from
disk: drag a `.tspi` on, get orbitable 3D trails, attitude-oriented markers, scrubbing,
and the footer event log — parsed and rendered with hand-rolled JS/WebGL, zero
dependencies, no build step. It exists for environments where Unity can't be approved or
binaries can't be delivered: the whole viewer is auditable source. `tspi serve` hosts the
same page over http with read-only `.tspi`/`.json` access plus `POST /api/run` /
`/api/validate`, and the page grows an in-page scenario editor with deterministic
resume-at-t — the browser becomes an edit-loop client of the real CLI. See `web/README.md`.

## Godot viewer (open-source engine, GDScript-only)

`godot/TspiViewer` is a playback-only Godot 4.4 project: same transport/scene/panel
feature set as the web viewer, rendered natively. The reader is a GDScript port of the
format (no Mono, no addons), contract-tested headless against the same golden file as
the Python and JS readers. See `godot/README.md`.

## Documentation

- `docs/WALKTHROUGH.md` — **start here**: one manifest driven through the whole
  pipeline (validate → run → inspect → analysis → browser viewer), real outputs at
  every stage
- `docs/FORMAT.md` — the `.tspi` container, normative (header/records/footer/trailer,
  append & recovery, layout-evolution rules)
- `docs/CONVENTIONS.md` — frames, quaternion, time, units, determinism (read first)
- `docs/ICD-NN.md` — interface control document for the NN guidance package
  (training-data record, runtime obs contract, policy delivery format, open items);
  render the SME review PDF with `python scripts/md2pdf.py` (PyMuPDF) or a briefing
  deck with `python scripts/md2pptx.py` (python-pptx); outputs git-ignored
- `docs/ARCHITECTURE.md` — component map, fidelity level, scaling numbers
- `schemas/` — JSON Schemas for manifests/models + validated examples

## Design invariants

1. Simulation is headless & deterministic (fixed-step RK4, f64, per-entity RNG streams);
   `manifest + models + seed + sim_version → byte-identical .tspi` on one platform
   (golden-locked in CI; cross-platform is tolerance-based via `tspi diff --tol-m`). Runs
   stream to disk as they integrate, so a sweep never buffers whole trajectories.
2. Viewers are pure playback clients: Unity through the same `Tspi.Core` reader, the web
   and Godot viewers through JS/GDScript ports of it (each contract-tested against the
   golden file / `tools/tspi_py`). Nothing on a screen was ever computed in a viewer.
3. Appends never rewrite bytes: torn appends recover (`tspi recover`, fuzz-tested at every
   truncation offset), live readers are safe, the footer chain keeps every historical index
   snapshot, and the persisted environment lets appended munitions fly in the original air mass.
4. Manifest evolution is additive (channel-based maneuvers, discriminated unions); record
   evolution is by `layout` id extending a fixed 64-byte prefix (old readers skip unknown
   layouts). Fidelity is kinematic 3-DoF + synthesized attitude, tagged in provenance — not
   aero-moment 6-DoF.
