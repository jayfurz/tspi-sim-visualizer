# Live streaming into the web viewer

The web viewer normally plays a finished `.tspi`. This directory makes it a
**live** display for a running simulator — a C++/Boost sim, or anything that can
write 64 bytes to a socket — without the viewer ever learning to simulate.

The trick is that nothing new is invented for the wire: a producer sends the
`.tspi` format's own 64-byte records (`docs/FORMAT.md`), so the viewer's
Hermite/slerp sampler is untouched and a live pose equals the replayed pose of
the same run, exactly. The wire contract is `PROTOCOL.md`.

```
sim tick ──push()──▶ batch ──WebSocket──▶ LiveTspiFile ──sampleAt()──▶ same renderer
                (64-B layout-1 records)   (web/viewer/tspi.js)
```

## Try it in 30 seconds (no C++ build)

Replay a recorded run *as if* it were live:

```sh
node tools/live-stream/replay_server.mjs runs/ship-to-air.tspi
# open the printed URL: http://localhost:8787/?ws=ws://localhost:8787/stream
```

The server also serves `web/viewer/` statically, so one process gives you the
page and the feed. `--rate 4` replays faster than real time, `--port` moves it.

## C++ producer (Boost.Beast, header-only)

`cpp/tspi_stream.hpp` is the whole integration — one header, no compiled Boost
libraries needed, no dependency on this repo's .NET code:

```cpp
#include "tspi_stream.hpp"

tspi::StreamConfig cfg;
cfg.port = 8787;
cfg.dt_ns = 20'000'000;                     // 50 Hz — must match your tick rate
cfg.epoch_unix_ns = wall_clock_of_sim_t0;
cfg.origin_lat_deg = 36.2; cfg.origin_lon_deg = -115.0;
cfg.dynamics = "6-dof rigid body (my_sim v4)";   // shown in the viewer header

tspi::StreamServer stream(cfg);
stream.start();                                   // io_context on its own thread
stream.add_entity({.ord = 0, .id = "blue-01", .team = "blue", .type = "aircraft"});

for (std::uint32_t i = 0; running; ++i) {
  integrate(dt);
  tspi::State s;
  s.pos[0] = north; s.pos[1] = east; s.pos[2] = down;      // f64 metres, NED
  s.vel[0] = vn;    s.vel[1] = ve;   s.vel[2] = vd;        // f32 m/s
  s.quat[0] = qw;   s.quat[1] = qx;  s.quat[2] = qy; s.quat[3] = qz;  // body->NED
  s.omega[0] = p;   s.omega[1] = q;  s.omega[2] = r;       // rad/s
  stream.push(0, i, s);
  stream.flush();                                 // one batch frame per tick
}
stream.end();
```

`add_entity` mid-run announces a launch; `event(t_ns, "intercept", src, dst)`
feeds the viewer's event log and scrub-bar ticks. All of these are safe to call
from the simulation thread — the header owns an `io_context` thread and hands
work to it.

Build and run the worked example (two aircraft and a missile that spawns at
t=20 s and kills its target):

```sh
cmake -S tools/live-stream/cpp -B build/live-stream -DCMAKE_BUILD_TYPE=Release
cmake --build build/live-stream
./build/live-stream/tspi_example_sim --port 8787 --rate 1 --duration 90
```

Then open `web/viewer/index.html`, type the `ws://` URL into the connect box on
the drop screen (or use `?ws=ws://localhost:8787/stream` when the page is
served), and the run appears as it flies.

## Recording a live run

A producer keeps no history, so without a sink a live engagement is gone once it
scrolls past. `tspi record` subscribes like any other viewer and lands the run in
the container:

```sh
tspi record ws://localhost:8787/stream -o runs/live.tspi     # Ctrl-C stops and finishes
tspi record ws://localhost:8787/stream --duration 120        # or stop after 2 minutes
```

It stops on the producer's `end`, on `--duration`, or on Ctrl-C, and in every case
writes a complete, valid file — including when the producer dies mid-stream. From
there the whole toolchain applies: play it back in any viewer, `tspi inspect` it,
`tspi diff` it, `tspi append` simulated munitions against it, analyse it with
`tools/tspi_py`.

Recording copies bytes rather than re-interpolating — the wire already carries the
container's records — so a recorded replay is *identical* to its source:

```sh
node tools/live-stream/replay_server.mjs runs/ship-to-air.tspi &
tspi record ws://localhost:8787/stream -o /tmp/rec.tspi
tspi diff runs/ship-to-air.tspi /tmp/rec.tspi --tol-m 0.001   # max delta 0.000E+000 m
```

What it does with imperfect input, all of it counted in provenance rather than
hidden: dropped frames are padded so `t = t0 + i*dt` stays exact (`gaps_filled`),
records for unannounced or non-finite entities are dropped (`records_dropped`),
duplicate/stale indices keep the first write, a mid-stream join is rebased so
samples keep their true sim time, and quaternions are made sign-continuous as the
container requires (`quats_sign_flipped`). Provenance carries the producer's own
`dynamics` tag — this toolchain did not compute the motion and does not claim to.

Memory stays flat regardless of run length: records spool to one temp file per
entity and stream into their blocks at the end.

Run a viewer and a recorder against the same producer at once — they are
independent subscribers.

## Consumers

Three, all applying the same rules:

- **Browser** — `web/viewer/`, connect box or `?ws=` (`LiveTspiFile` in `tspi.js`).
- **Unity** — add `TspiLiveClient` to the playback object, or enable the one already in
  the sample scene (`unity/README.md`). Editor/standalone only; WebGL can't open a
  ClientWebSocket.
- **`tspi record`** — the sink that writes the stream to a `.tspi` (see below).

## What the viewer does with a live source

- **LIVE badge** rides the head of the stream, one sample interval behind so
  there is always a bracketing pair to interpolate.
- **Scrub back at any time** — the buffered trail is fully scrubbable; scrubbing
  or pausing detaches from the head, the `LIVE` button re-attaches.
- **Entities appear when announced** and their trails grow by `bufferSubData`;
  the ground grid re-sizes once the engagement's scale is known.
- **Dropped frames** are padded so the clock stays exact, and the fill count is
  shown next to the record count rather than hidden.
- **Reconnects** automatically every 2 s if the producer restarts.

## Ports of call

| file | what |
| --- | --- |
| `PROTOCOL.md` | the wire contract — read this before writing a producer |
| `cpp/tspi_stream.hpp` | header-only C++ producer (Boost.Beast + Asio) |
| `cpp/example_sim.cpp` | worked example: orbit, inbound, launch, intercept |
| `replay_server.mjs` | Node producer that replays a `.tspi`; also serves the viewer |
| `../../web/viewer/tspi.js` | `LiveTspiFile` — the browser consumer |
| `../../src/Tspi.Core/Runtime/Live/` | `LiveTspiSource` — the .NET/Unity consumer |
| `../../unity/TspiViewer/Assets/Scripts/TspiLiveClient.cs` | the Unity component that subscribes |
| `../../web/viewer/tests/live.test.mjs` | proves live == replay, bit-for-bit |
| `../../src/Tspi.Sim/Live/LiveRecorder.cs` | the `tspi record` sink (stream → `.tspi`) |
| `../../src/Tspi.Tests/RecordTests.cs` | recorder tests over a real WebSocket |

## Tests

```sh
node web/viewer/tests/live.test.mjs runs/ship-to-air.tspi          # viewer side
dotnet test src/Tspi.Tests --filter FullyQualifiedName~RecordTests # recorder side
```

The viewer test covers: file → wire → `LiveTspiFile` with every pose compared
against the file reader; duplicate/stale/gapped/unknown-`ord` frames; joining
mid-stream; and a full pass over a real WebSocket against `replay_server.mjs`.
The recorder tests drive `tspi record` from a hand-rolled WebSocket producer and
read the written file back: round-trip fidelity, gap padding, mid-stream rebase,
duplicate/stale/unknown drops, quaternion sign continuity, `--duration` stops, a
producer that dies mid-stream, and the protocol/ordering errors. `scripts/e2e.sh`
records a replayed run and `tspi diff`s it against the original.

## Limits (deliberate)

- **No history backfill** for late joiners — see `PROTOCOL.md`. Run `tspi record`
  from the start if the whole run matters; a recorder and viewers are independent
  subscribers.
- **One-way.** Viewers never command the sim.
- **Plain `ws://`, no compression** — a lab-LAN tool. Put a TLS-terminating
  reverse proxy in front for `wss://`.
- A slow viewer's send queue is capped (`Session::kMaxQueue`); overflow drops the
  oldest *record* batches, never control messages, and the viewer's gap-padding
  keeps its clock exact.
