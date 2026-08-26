# .tspi trajectory format — v1 (normative)

A `.tspi` file is a self-describing, append-friendly container of time-space-position-
information (TSPI) for many entities. It is designed to be **memory-mapped**: any sample
of any entity is an O(1) indexed lookup, and appends are O(new samples) with no rewrite
of existing bytes.

All multi-byte fields are **little-endian**. Times are **int64 nanoseconds** relative to
the header epoch. Lengths/offsets are byte counts from the start of the file.

## File layout

```
+-----------------------------+  offset 0
| Header (128 B, fixed)       |
+-----------------------------+
| Entity block 0              |  [block header 32 B][sampleCount * stride records]
| Entity block 1              |
| ...                         |
+-----------------------------+
| Footer #0 (UTF-8 JSON)      |  entity table + events + provenance
+-----------------------------+
| Trailer #0 (32 B, fixed)    |  locates Footer #0
+-----------------------------+   <-- a complete file ends here
| Entity block k    (append)  |
| ...                         |
| Footer #1 (JSON)            |  = Footer #0 entries + appended, prev_footer -> #0
| Trailer #1 (32 B)           |
+-----------------------------+  EOF
```

An append writes new entity blocks at EOF, then a new footer (superset of the previous,
with `prev_footer_offset` pointing back), then a new trailer. **Old bytes are never
modified.** Consequences: torn appends are recoverable, concurrent readers holding an
mmap are unaffected, and every historical index snapshot remains in the file.

## Header (128 bytes)

| offset | size | field | notes |
|-------:|-----:|-------|-------|
| 0  | 4  | magic | ASCII `TSPI` |
| 4  | 4  | version | u32 = 1 |
| 8  | 4  | flags | u32, reserved (0) |
| 12 | 4  | reserved | 0 |
| 16 | 8  | dt_ns | u64 fixed sample period, shared by all blocks |
| 24 | 8  | epoch_unix_ns | i64 UTC of sim t=0 |
| 32 | 8  | origin_lat_deg | f64 |
| 40 | 8  | origin_lon_deg | f64 |
| 48 | 8  | origin_alt_m | f64 (ellipsoidal) |
| 56 | 32 | manifest_sha256 | SHA-256 of the producing manifest |
| 88 | 40 | reserved | zero-filled to 128 |

The header is written once at create time and never modified.

## Entity block

Block header (32 bytes), immediately followed by the records:

| offset | size | field |
|-------:|-----:|-------|
| 0  | 4 | magic ASCII `EBLK` |
| 4  | 4 | ord (u32) |
| 8  | 2 | layout (u16) |
| 10 | 2 | stride (u16) |
| 12 | 4 | reserved |
| 16 | 8 | t0_ns (i64) — first sample time |
| 24 | 8 | sample_count (u64) |

The footer entity table's `offset` points at the first **record** (i.e. block start + 32).

## Record — layout 1 (stride 64 B, one cache line)

| offset | size | field | type |
|-------:|-----:|-------|------|
| 0  | 24 | pos_ned  | f64 × 3 (m) |
| 24 | 12 | vel_ned  | f32 × 3 (m/s) |
| 36 | 16 | quat     | f32 × 4, W-first, body→NED, Hamilton |
| 52 | 12 | omega_body | f32 × 3 (rad/s) |

Time is **implicit**: sample `i` is at `t0_ns + i * dt_ns`. There are no per-record
timestamps and no per-record flags — discrete happenings live in the footer event log.

Position is f64 (TSPI needs sub-meter truth at 10^5 m offsets); velocity/attitude/rates
are f32 (small magnitudes; f32 gives ~µrad attitude, ~mm/s velocity).

### Layout evolution

New record shapes get a new `layout` id and **must extend layout 1's 64-byte prefix
without reordering it**. An old reader steps by `stride` and reads the prefix it
understands, ignoring the tail. Example future layout: `2` = layout 1 + position
covariance upper-triangle (6 × f32) → stride 96.

## Footer (UTF-8 JSON)

Read once per open; KB-sized, so JSON's parse cost is irrelevant and its debuggability
and additive evolution are worth it. Unknown keys must be ignored by readers.

```json
{
  "format": { "version": 1 },
  "entities": [
    { "ord": 0, "id": "blue-01", "team": "blue", "type": "aircraft", "model": "generic-fighter",
      "parent": null, "t0_ns": 0, "samples": 7001, "offset": 160, "stride": 64, "layout": 1 }
  ],
  "events": [
    { "t_ns": 22810000000, "kind": "launch", "src": 2, "dst": 1, "data": {} }
  ],
  "provenance": [
    { "op": "run", "sim_version": "0.1.0", "dynamics": "kinematic-3dof+synth-attitude",
      "manifest_sha256": "…", "seed": 42, "models": { "generic-fighter": "…" } }
  ],
  "environment": { "atmosphere": "exp8500", "wind": { "layers": [ … ], "gusts": { … } } },
  "prev_footer_offset": null,
  "prev_footer_len": null
}
```

`src`/`dst` reference entity `ord`. Event `kind`s in v1: `launch`, `cpa`, `intercept`,
`ground_impact`, `expire`, `killed` (src = the munition destroyed by a
munition-vs-munition intercept, dst = its killer; the victim's block ends at the kill
time). `miss_m` (sub-dt refined closest-approach distance) rides in `data`. The provenance `models` map holds the SHA-256 of every vehicle model **and
guidance policy** file the run resolved — "same manifest, same seed" only pins the
output if the weights that flew the munitions are pinned too. `environment` records the atmosphere + wind the run used and is carried forward
across appends, so a later `tspi append` flies its munitions in the same air mass. The
`dynamics` provenance tag marks the fidelity level. Translation is always kinematic
point-mass; the attitude fragment says how aircraft attitude was produced:

- `kinematic-3dof+synth-attitude` — attitude synthesized from the flight path, body
  rates finite-differenced (v1 default).
- `kinematic-3dof+rigid-attitude` — every aircraft's attitude integrated from rigid-body
  rotational EOM (models with a `rotational` block); recorded body rates are the
  integrated ω.
- `kinematic-3dof+mixed-attitude` — scenario mixes both kinds of aircraft.

Munition attitude is velocity-aligned (synthesized) in all three cases.

Files created by `tspi import` (externally measured TSPI, resampled onto the fixed dt
grid) carry an `op: "import"` provenance record instead: no `seed`/`models`, a `source`
+ `source_sha256` pair naming the input data, and the header's `manifest_sha256` slot
holds that same source-file hash. Their `dynamics` tag is one of:

- `measured+input-attitude` — attitude columns came from the source data (body rates
  from the source when present, else finite-differenced at write).
- `measured+synth-attitude` — the source carried no attitude; yaw/pitch follow the
  resampled velocity with coordinated-turn bank, rates finite-differenced.

An imported file has no `environment` field (the real air mass is unknown), so a later
`tspi append` flies its munitions in the default environment unless one is added.

Files created by `tspi record` (a live stream captured off the wire — see
`tools/live-stream/PROTOCOL.md`) carry an `op: "record"` provenance record: no
`seed`/`models`, a `source` naming the producer's `ws://` endpoint, the wire `protocol`
version, and the honest counters `samples`, `gaps_filled` (samples synthesized over
dropped frames), `records_dropped`, plus `stop_reason`. Their `dynamics` tag is
whatever the producer declared in its `hello` — this toolchain did not compute the
motion and does not claim to. The header's `manifest_sha256` slot holds the SHA-256 of
that `hello` message (no manifest exists), which identifies the stream configuration.
A recorded file has no `environment` field for the same reason an imported one does
not. Streams carry the same 64-byte layout-1 records this container stores, so
recording copies bytes rather than re-interpolating; the recorder does enforce the
sign-continuous quaternion rule above (`quats_sign_flipped` counts the fixes).

## Trailer (32 bytes, always the last 32 bytes of the file)

| offset | size | field |
|-------:|-----:|-------|
| 0  | 8 | footer_offset (u64) |
| 8  | 8 | footer_len (u64) |
| 16 | 4 | footer_crc32 (u32, IEEE, over the footer bytes) |
| 20 | 4 | reserved |
| 24 | 8 | magic ASCII `TSPIFTR1` |

### How a reader finds the footer

You never scan for the JSON. Seek to `EOF − 32`, read the fixed trailer, check the
magic, then read exactly `footer_len` bytes at `footer_offset` and verify the CRC. Write
order (blocks → footer → trailer) guarantees a valid trailer implies a complete footer.
This is the same trick as ZIP's end-of-central-directory and Parquet's footer.

### Recovery (torn append)

If the trailer at EOF is invalid, scan backward for the previous `TSPIFTR1` magic (the
prior append left an intact trailer mid-file), validate its footer adjacency + CRC, and
truncate the file just past it. `tspi recover <file> --apply` does this.

## Determinism contract

`manifest + models + seed + sim_version` → **byte-identical** file **on the same
platform** (enforced by `DeterminismTests` and the golden byte-lock). The streaming and
buffered writers are byte-for-byte equivalent, so the guarantee holds regardless of which
write path produced the file. Cross-platform output is *not* bit-exact — see
docs/CONVENTIONS.md; use `tspi diff --tol-m` for portable comparison. Changing the record
layout, the sim math, or the serialization is a deliberate act: bump `version` (format) or
`sim_version` and regenerate `tools/tspi_py/tests/data/golden-v1.tspi`.
