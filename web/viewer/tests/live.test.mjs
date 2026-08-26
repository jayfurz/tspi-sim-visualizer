// Live-stream reader test: LiveTspiFile must be indistinguishable from TspiFile.
//
// The whole point of streaming the file format's own 64-byte records is that a
// live pose and a replayed pose are the same number, so this test feeds a real
// .tspi through the wire encoding and demands bit-identical sampling — plus an
// end-to-end pass over an actual WebSocket against tools/live-stream/replay_server.mjs.
//
// Usage: node live.test.mjs <file.tspi> [more.tspi ...]
import { createRequire } from 'node:module';
import { readFileSync } from 'node:fs';
import { spawn } from 'node:child_process';
import { dirname, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const require = createRequire(import.meta.url);
const Tspi = require('../tspi.js');
const here = dirname(fileURLToPath(import.meta.url));
const SERVER = resolve(here, '../../../tools/live-stream/replay_server.mjs');

let checks = 0;
function eq(a, b, what) {
  checks++;
  if (a !== b) throw new Error(`${what}: got ${a}, want ${b}`);
}
function vecEq(a, b, what) {
  eq(a.length, b.length, `${what}.length`);
  a.forEach((v, i) => eq(v, b[i], `${what}[${i}]`));
}
// Rebased entities compute u = (t - t0)/dt from a different t0, so the last ulp
// can differ; positions still agree to far below a micron.
function vecClose(a, b, tol, what) {
  eq(a.length, b.length, `${what}.length`);
  a.forEach((v, i) => {
    checks++;
    if (!(Math.abs(v - b[i]) <= tol)) throw new Error(`${what}[${i}]: got ${v}, want ${b[i]} ±${tol}`);
  });
}
function loadFile(path) {
  const raw = readFileSync(path);
  return Tspi.parse(raw.buffer.slice(raw.byteOffset, raw.byteOffset + raw.byteLength), path);
}

const entityDesc = (e) => ({
  ord: e.ord, id: e.id, team: e.team, type: e.type, model: e.model,
  parent: e.parent, t0_ns: e.t0Ns, layout: e.layout,
});
const helloFor = (file, entities) => ({
  type: 'hello', protocol: Tspi.LIVE_PROTOCOL,
  dt_ns: file.header.dtNs, epoch_unix_ns: String(file.header.epochUnixNs),
  origin: {
    lat_deg: file.header.originLatDeg, lon_deg: file.header.originLonDeg,
    alt_m: file.header.originAltM,
  },
  entities: entities.map(entityDesc),
});

function encodeBatch(items) {
  const buf = new ArrayBuffer(4 + items.length * 72);
  const dv = new DataView(buf);
  dv.setUint32(0, items.length, true);
  items.forEach(({ ord, index, rec }, k) => {
    const b = 4 + k * 72;
    dv.setUint32(b, ord, true);
    dv.setUint32(b + 4, index, true);
    const o = b + 8;
    for (let i = 0; i < 3; i++) dv.setFloat64(o + i * 8, rec.pos[i], true);
    for (let i = 0; i < 3; i++) dv.setFloat32(o + 24 + i * 4, rec.vel[i], true);
    for (let i = 0; i < 4; i++) dv.setFloat32(o + 36 + i * 4, rec.quat[i], true);
    for (let i = 0; i < 3; i++) dv.setFloat32(o + 52 + i * 4, rec.omega[i], true);
  });
  return buf;
}

// Every sampleable entity's every record, sampled against the file reader.
function assertMatchesFile(file, live, what) {
  const sampleable = file.entities.filter((e) => e.layout === Tspi.LAYOUT_6DOF_V1 && e.samples > 0);
  eq(live.entities.length, sampleable.length, `${what}: entity count`);
  for (const fe of sampleable) {
    const le = live.findEntity(fe.id);
    if (!le) throw new Error(`${what}: entity ${fe.id} missing from live`);
    eq(le.samples, fe.samples, `${what}: ${fe.id}.samples`);
    eq(le.t0Ns, fe.t0Ns, `${what}: ${fe.id}.t0_ns`);
    eq(live.startSec(le), file.startSec(fe), `${what}: ${fe.id}.start`);
    eq(live.endSec(le), file.endSec(fe), `${what}: ${fe.id}.end`);
    // Raw records are f64/f32 round-trips through the wire: exactly equal.
    for (const i of [0, 1, (fe.samples / 2) | 0, fe.samples - 2, fe.samples - 1].filter((i) => i >= 0)) {
      const a = file.readSample(fe, i), b = live.readSample(le, i);
      vecEq(b.pos, a.pos, `${what}: ${fe.id}[${i}].pos`);
      vecEq(b.vel, a.vel, `${what}: ${fe.id}[${i}].vel`);
      vecEq(b.quat, a.quat, `${what}: ${fe.id}[${i}].quat`);
      vecEq(b.omega, a.omega, `${what}: ${fe.id}[${i}].omega`);
    }
    // Interpolated poses — same Hermite/slerp code, so also exactly equal.
    const t0 = file.startSec(fe), t1 = file.endSec(fe);
    for (let k = 0; k <= 97; k++) {
      const t = t0 + ((t1 - t0) * k) / 97;
      const a = file.sampleAt(fe, t, false), b = live.sampleAt(le, t, false);
      if ((a === null) !== (b === null)) throw new Error(`${what}: ${fe.id} aliveness differs at t=${t}`);
      if (!a) continue;
      vecEq(b.pos, a.pos, `${what}: ${fe.id}@${t.toFixed(3)}.pos`);
      vecEq(b.vel, a.vel, `${what}: ${fe.id}@${t.toFixed(3)}.vel`);
      vecEq(b.quat, a.quat, `${what}: ${fe.id}@${t.toFixed(3)}.quat`);
      vecEq(b.omega, a.omega, `${what}: ${fe.id}@${t.toFixed(3)}.omega`);
    }
  }
}

// ---- 1. offline: file -> wire encoding -> LiveTspiFile ----
function testOffline(path) {
  const file = loadFile(path);
  const sampleable = file.entities.filter((e) => e.layout === Tspi.LAYOUT_6DOF_V1 && e.samples > 0);
  const atT0 = sampleable.filter((e) => file.startSec(e) <= file.timeSpan().min + 1e-9);
  const live = new Tspi.LiveTspiFile(helloFor(file, atT0), 'live');
  eq(live.dtSec, file.dtSec, 'dtSec');
  eq(String(live.header.epochUnixNs), String(file.header.epochUnixNs), 'epoch_unix_ns');

  // Interleave entities the way a producer does: one batch per wall tick, each
  // entity's index counted from its own t0 (munitions spawn mid-stream).
  const span = file.timeSpan();
  const firstTick = (e) => Math.round((file.startSec(e) - span.min) / file.dtSec);
  const ticks = Math.max(...sampleable.map((e) => firstTick(e) + e.samples));
  for (let n = 0; n < ticks; n++) {
    const items = [];
    for (const e of sampleable) {
      const i = n - firstTick(e);
      if (i < 0 || i >= e.samples) continue;
      if (i === 0 && !atT0.includes(e)) live.ingestJson({ type: 'entity', entity: entityDesc(e) });
      items.push({ ord: e.ord, index: i, rec: file.readSample(e, i) });
    }
    if (items.length) live.ingestBatch(encodeBatch(items));
  }
  file.events.forEach((ev) => live.ingestJson({ type: 'event', ...ev }));
  live.ingestJson({ type: 'end' });

  eq(live.ended, true, 'ended');
  eq(live.gaps, 0, 'gaps');
  eq(live.events.length, file.events.length, 'event count');
  assertMatchesFile(file, live, 'offline');
  console.log(`  offline ingest: ${live.received} records, ${live.entities.length} entities ✓`);
}

// ---- 2. degenerate wire conditions ----
function testWireEdges(path) {
  const file = loadFile(path);
  const e0 = file.entities.find((e) => e.layout === Tspi.LAYOUT_6DOF_V1 && e.samples > 4);
  const live = new Tspi.LiveTspiFile(helloFor(file, [e0]), 'edges');
  const le = live.findEntity(e0.id);
  const put = (index) => live.ingestBatch(encodeBatch([{ ord: e0.ord, index, rec: file.readSample(e0, index) }]));

  put(0); put(1);
  eq(le.samples, 2, 'two records in');
  put(1);                       // duplicate
  eq(le.samples, 2, 'duplicate index dropped');
  put(0);                       // late/out-of-order
  eq(le.samples, 2, 'stale index dropped');
  put(4);                       // gap: 2 and 3 were dropped on the wire
  eq(le.samples, 5, 'gap padded to keep t = t0 + i*dt exact');
  eq(live.gaps, 2, 'gap count reported');
  vecEq(live.readSample(le, 4).pos, file.readSample(e0, 4).pos, 'post-gap record');
  // Records for an unannounced entity are dropped rather than guessed at.
  const before = live.received;
  live.ingestBatch(encodeBatch([{ ord: 9999, index: 0, rec: file.readSample(e0, 0) }]));
  eq(live.received, before, 'unknown ord dropped');
  console.log('  wire edges: duplicate/stale/gap/unknown-ord handled ✓');
}

// ---- 3. joining a run already in progress ----
// The viewer is opened mid-engagement: the producer's indices start well above
// zero. The trail must start at the join point with every sample still carrying
// its true sim time, and nothing may be padded.
function testMidStreamJoin(path) {
  const file = loadFile(path);
  const e0 = file.entities.find((e) => e.layout === Tspi.LAYOUT_6DOF_V1 && e.samples > 200);
  const join = 137;
  const live = new Tspi.LiveTspiFile(helloFor(file, [e0]), 'join');
  const le = live.findEntity(e0.id);
  for (let i = join; i < e0.samples; i++)
    live.ingestBatch(encodeBatch([{ ord: e0.ord, index: i, rec: file.readSample(e0, i) }]));

  eq(live.gaps, 0, 'join: nothing padded');
  eq(le.samples, e0.samples - join, 'join: sample count from the join point');
  eq(live.startSec(le), file.startSec(e0) + join * file.dtSec, 'join: t0 is the join sample time');
  eq(live.endSec(le), file.endSec(e0), 'join: end time unchanged');
  vecEq(live.readSample(le, 0).pos, file.readSample(e0, join).pos, 'join: first record');
  // Absolute sim time is preserved, so poses match the file over the overlap.
  const t0 = live.startSec(le), t1 = live.endSec(le);
  for (let k = 0; k <= 50; k++) {
    const t = t0 + ((t1 - t0) * k) / 50;
    vecClose(live.sampleAt(le, t, false).pos, file.sampleAt(e0, t, false).pos, 1e-9,
      `join: pose@${t.toFixed(3)}`);
  }
  console.log(`  mid-stream join at index ${join}: rebased, ${le.samples} records, 0 gaps ✓`);
}

// ---- 4. end-to-end over a real WebSocket against the replay server ----
async function testOverSocket(path, port) {
  const file = loadFile(path);
  const proc = spawn(process.execPath, [SERVER, path, '--port', String(port), '--rate', '400', '--tick-ms', '10'],
    { stdio: ['ignore', 'pipe', 'inherit'] });
  try {
    await new Promise((ok, fail) => {
      proc.stdout.on('data', (d) => { if (String(d).includes('stream:')) ok(); });
      proc.on('exit', (c) => fail(new Error(`server exited early (${c})`)));
      setTimeout(() => fail(new Error('server did not start')), 10000);
    });

    const live = await new Promise((ok, fail) => {
      const ws = new WebSocket(`ws://localhost:${port}/stream`);
      ws.binaryType = 'arraybuffer';
      let f = null;
      const timer = setTimeout(() => fail(new Error('stream did not end in time')), 60000);
      ws.onerror = (e) => fail(new Error('socket error: ' + (e.message || 'unknown')));
      ws.onmessage = (ev) => {
        try {
          if (typeof ev.data === 'string') {
            const msg = JSON.parse(ev.data);
            if (msg.type === 'hello') { f = new Tspi.LiveTspiFile(msg, 'ws'); return; }
            f.ingestJson(msg);
            if (msg.type === 'end') { clearTimeout(timer); ws.close(); ok(f); }
          } else {
            f.ingestBatch(ev.data);
          }
        } catch (e) { clearTimeout(timer); fail(e); }
      };
    });

    eq(live.gaps, 0, 'socket: no gaps');
    eq(live.events.length, file.events.length, 'socket: event count');
    assertMatchesFile(file, live, 'socket');
    console.log(`  websocket replay: ${live.received} records, ` +
      `${live.events.length} events, ${live.entities.length} entities ✓`);
  } finally {
    proc.kill();
  }
}

const paths = process.argv.slice(2);
if (!paths.length) {
  console.error('usage: node live.test.mjs <file.tspi> [...]');
  process.exit(2);
}
let port = 8830;
for (const path of paths) {
  console.log(path);
  testOffline(path);
  testWireEdges(path);
  testMidStreamJoin(path);
  await testOverSocket(path, port++);
}
console.log(`live: ${checks} checks passed across ${paths.length} file(s)`);
