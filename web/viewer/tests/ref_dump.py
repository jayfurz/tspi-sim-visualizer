"""Dump reference values from the trusted Python reader (tools/tspi_py) for the
JS parser test. Usage:
    python ref_dump.py <file.tspi> [...] > ref.json
    node parser.test.mjs ref.json <file.tspi> [...]
"""
import json
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), "..", "..", "..", "tools", "tspi_py"))
from tspi_py.reader import TspiFile

out = {}
for path in sys.argv[1:]:
    f = TspiFile(path)
    ents = []
    for e in f.entities.values():
        arr = f.samples(e.id)
        picks = sorted(set([0, e.samples // 2, e.samples - 1]))
        ents.append({
            "ord": e.ord, "id": e.id, "team": e.team, "type": e.type,
            "model": e.model, "t0_ns": e.t0_ns, "samples": e.samples,
            "offset": e.offset, "stride": e.stride, "layout": e.layout,
            "picks": [
                {
                    "i": int(i),
                    "pos": [float(v) for v in arr["pos"][i]],
                    "vel": [float(v) for v in arr["vel"][i]],
                    "quat": [float(v) for v in arr["quat"][i]],
                    "omega": [float(v) for v in arr["omega"][i]],
                }
                for i in picks
            ],
        })
    out[path] = {
        "header": {
            "version": f.version, "dt_ns": f.dt_ns, "epoch_unix_ns_str": str(f.epoch_unix_ns),
            "origin_lat_deg": f.origin_lat_deg, "origin_lon_deg": f.origin_lon_deg,
            "origin_alt_m": f.origin_alt_m, "manifest_sha256": f.manifest_sha256,
        },
        "entities": ents,
        "events": [{"t_ns": ev.t_ns, "kind": ev.kind, "src": ev.src, "dst": ev.dst} for ev in f.events],
    }
print(json.dumps(out))
