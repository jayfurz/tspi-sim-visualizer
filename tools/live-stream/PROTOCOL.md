# Live stream protocol (`tspi-live/1`)

How a running simulator pushes state to `web/viewer/` while it flies, instead of
handing it a finished `.tspi`. One WebSocket, no framing library, no schema
compiler: **the records on the wire are the file format's own 64-byte layout-1
records** (`docs/FORMAT.md` §"Record — layout 1"), so a producer writes them with
one `memcpy` and the viewer samples them with the same Hermite/slerp code it uses
for recorded files. A live pose and the replayed pose of the same run are the
same number — `web/viewer/tests/live.test.mjs` asserts it bit-for-bit.

- Transport: WebSocket. **Text** frames carry JSON control messages, **binary**
  frames carry record batches.
- Byte order: little-endian, matching the file format.
- Reference producers: `tools/live-stream/cpp/` (C++/Boost.Beast, header-only)
  and `tools/live-stream/replay_server.mjs` (Node, replays a `.tspi`).
- Reference consumers: `Tspi.LiveTspiFile` in `web/viewer/tspi.js` (the viewer) and
  `LiveRecorder` in `src/Tspi.Sim/Live/` (the `tspi record` sink, which writes the
  stream back into a `.tspi`). Both apply the consumer rules below identically.

## Handshake

The producer sends **`hello`** first, before any records:

```json
{ "type": "hello", "protocol": 1, "name": "boost example sim",
  "dt_ns": 20000000,
  "epoch_unix_ns": "1787452800000000000",
  "origin": { "lat_deg": 36.2, "lon_deg": -115.0, "alt_m": 0 },
  "extent_m": 25000,
  "dynamics": "6-dof rigid body (my_sim v4)",
  "entities": [ { "ord": 0, "id": "blue-01", "team": "blue", "type": "aircraft",
                  "model": "f-16", "parent": null, "t0_ns": 0, "layout": 1 } ] }
```

- `dt_ns` — the sample interval. **Time is implicit, exactly as in the file
  format**: sample `i` of an entity is at `t0_ns + i * dt_ns`. There are no
  per-record timestamps.
- `epoch_unix_ns` — wall clock of sim `t=0`, sent as a **string**: absolute
  nanoseconds overflow JavaScript's 2^53 integers.
- `extent_m` — optional hint for the initial ground-grid size.
- `dynamics` — the honesty tag shown in the viewer's header; say what actually
  produced the motion.
- `entities` — those alive at the start. Late ones are announced as they spawn.

## Control messages (text frames)

| message | when | body |
| --- | --- | --- |
| `entity` | an entity spawns mid-run (a launch) | `{"type":"entity","entity":{…same fields as above…}}` |
| `event` | a discrete happening | `{"type":"event","t_ns":20000000000,"kind":"launch","src":0,"dst":1,"data":{"miss_m":2.4}}` |
| `end` | the run is over | `{"type":"end"}` |

The entity descriptor is **nested** under `entity` so that an entity's own `type`
(`aircraft`/`ship`/`munition`) cannot collide with the message envelope's `type`.

`event.kind` uses the same vocabulary as the `.tspi` footer event log (`launch`,
`cpa`, `intercept`, `ground_impact`, …); `src`/`dst` are entity `ord`s. Events
show up in the viewer's event panel and as scrub-bar ticks.

## Record batches (binary frames)

One frame per producer tick, carrying every entity's sample for that tick:

```
[u32 count]  then count × ( [u32 ord] [u32 sample_index] [64-byte record] )
```

`4 + count * 72` bytes total. The 64-byte record is layout 1 verbatim:

| offset | size | field |
| --- | --- | --- |
| 0  | 24 | position NED, `f64[3]`, metres |
| 24 | 12 | velocity NED, `f32[3]`, m/s — the Hermite tangents |
| 36 | 16 | attitude quaternion `f32[4]` **w,x,y,z**, body→NED |
| 52 | 12 | body rates `f32[3]`, rad/s |

`sample_index` counts that entity's own samples from its `t0_ns` — it is **not** a
global tick counter, and each entity's index restarts at 0 at its own spawn.

## Consumer rules

These are what `LiveTspiFile` does; a different consumer should match them.

- **Records for an unannounced `ord` are dropped.** Announce before you push.
- **Duplicate or stale `sample_index`** (≤ the last one stored) is dropped.
- **A forward gap** (a dropped frame) is padded by repeating the last sample, so
  `t = t0 + i*dt` stays exact. Padding is counted (`gaps`), never hidden.
- **Joining a run in progress** is normal: the first record received for an
  entity rebases local storage to that index and moves the entity's `t0_ns` to
  that record's true time. The trail starts where the viewer joined, and every
  sample keeps its real sim timestamp.
- The viewer renders one sample interval behind the newest record, so there is
  always a bracketing pair to interpolate between.
- Records carrying non-finite values are dropped (and the resulting hole padded)
  rather than written into a file.
- **Quaternions should be sign-continuous** (`dot(q_i, q_i+1) >= 0`) so playback
  slerp never takes the long way round. Producers that build attitude per-sample
  often are not — `tspi record` flips signs as needed on the way into the file and
  counts the fixes in provenance, and viewer slerp is shortest-path regardless.

## What the protocol deliberately does not do

- **No history backfill.** A viewer that joins late sees the run from its join
  point on. Run `tspi record ws://…` from the start if the full run matters — a
  recorder is just another subscriber, so it costs the producer one more socket,
  and the recorded file replays identically in the same viewer.
- **No client→server channel.** Viewers never command the sim; control stays
  wherever the sim's own controls are. (The scenario editor's run loop is the
  separate `tspi serve` HTTP API, `web/README.md`.)
- **No compression, no TLS.** Intended for a lab LAN. Terminate TLS at a reverse
  proxy (`wss://`) if the link leaves it.
