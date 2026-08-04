#!/usr/bin/env python3
"""The docs/WALKTHROUGH.md §4 analysis snippet as a runnable, shell-neutral script
(bash heredocs don't exist in PowerShell): zero-copy read of a run plus the
launch-centred DCV fly-out summary.

    python scripts/walkthrough_analysis.py runs/ship-to-air.tspi
"""

import sys

from tspi_py import TspiFile
from tspi_py.dcv import dcv_flyouts

path = sys.argv[1] if len(sys.argv) > 1 else "runs/ship-to-air.tspi"
f = TspiFile(path)
munition_ids = [e.id for e in f.entities.values() if e.type == "munition"]
print(f)
for mid in munition_ids:
    arr = f.samples(mid)
    peak = max((arr["vel"] ** 2).sum(axis=1)) ** 0.5
    print(f"{mid}: peak speed {peak:.0f} m/s")
for fly in dcv_flyouts(path):
    m = fly.munition
    print(f"{fly.launch.munition_id} in DCV: apogee {m.pos_dcv_m[:, 2].max():.0f} m, "
          f"terminal at {m.pos_dcv_m[-1, 0] / 1000:.1f} km downrange, "
          f"{fly.outcome.terminal} (miss {fly.outcome.miss_m:.1f} m)")
