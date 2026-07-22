# tspi-py

Zero-copy numpy reader for `.tspi` trajectory files (see `docs/FORMAT.md`).

```bash
pip install -e "tools/tspi_py[test,arrow]"
```

```python
from tspi_py import TspiFile

f = TspiFile("runs/intercept-0042.tspi")
f.entities                    # {'blue-01': Entity(...), ...}
arr = f.samples("blue-01")    # structured array: pos f64x3, vel f32x3, quat f32x4, omega f32x3
t = f.times("blue-01")        # seconds since header epoch (implicit fixed dt)
f.events                      # launch / cpa / intercept / ... with miss_m payloads
f.provenance                  # manifest+model hashes, seed, sim version per write/append

f.to_arrow("blue-01")         # pyarrow Table (requires the 'arrow' extra) -> parquet etc.
```

Derived views (rebuilt from the runs on every call — the `.tspi` files stay the
single source of truth):

```python
from tspi_py import engagements, dcv_flyouts

engagements("run.tspi")       # tspi-engagement/1: one record per launch event (docs/ICD-NN.md)
dcv_flyouts("run.tspi")       # tspi-dcv/1: the fly-out in launch-centred downrange/
                              # crossrange/vertical coordinates (see tspi_py/dcv.py)
```

The mmap is read-only and lazy: a 226 MB sweep directory on the HPC box costs page-cache,
not RAM copies. `tests/` runs against the committed golden file written by the C# sim —
the cross-language contract test.
