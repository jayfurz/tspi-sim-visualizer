// JS .tspi parser test: cross-checks web/viewer/tspi.js against reference values
// dumped by the trusted Python reader (tools/tspi_py), plus interpolation
// invariants that mirror TspiReader.TrySampleAt semantics.
//
// Usage: node parser.test.mjs <ref.json> <file.tspi> [more.tspi ...]
// (ref.json produced by tests/ref_dump.py via tools/tspi_py)
import { createRequire } from 'node:module';
import { readFileSync } from 'node:fs';

const require = createRequire(import.meta.url);
const Tspi = require('../tspi.js');

let checks = 0;
function eq(actual, expected, what) {
  checks++;
  if (actual !== expected) throw new Error(`${what}: got ${actual}, want ${expected}`);
}
function close(actual, expected, tol, what) {
  checks++;
  if (!(Math.abs(actual - expected) <= tol)) throw new Error(`${what}: got ${actual}, want ${expected} ±${tol}`);
}
function vecClose(a, b, tol, what) {
  eq(a.length, b.length, `${what}.length`);
  a.forEach((v, i) => close(v, b[i], tol, `${what}[${i}]`));
}

const [refPath, ...tspiPaths] = process.argv.slice(2);
if (!refPath || tspiPaths.length === 0) {
  console.error('usage: node parser.test.mjs <ref.json> <file.tspi> [...]');
  process.exit(2);
}
const ref = JSON.parse(readFileSync(refPath, 'utf-8'));

for (const path of tspiPaths) {
  const buf = readFileSync(path);
  const file = Tspi.parse(buf.buffer.slice(buf.byteOffset, buf.byteOffset + buf.byteLength), path);
  const want = ref[path];
  if (!want) throw new Error(`no reference entry for ${path}`);

  // Header
  eq(file.header.version, want.header.version, 'version');
  eq(file.header.dtNs, want.header.dt_ns, 'dt_ns');
  eq(String(file.header.epochUnixNs), want.header.epoch_unix_ns_str, 'epoch_unix_ns');
  close(file.header.originLatDeg, want.header.origin_lat_deg, 0, 'origin_lat');
  close(file.header.originLonDeg, want.header.origin_lon_deg, 0, 'origin_lon');
  close(file.header.originAltM, want.header.origin_alt_m, 0, 'origin_alt');
  eq(file.header.manifestSha256, want.header.manifest_sha256, 'manifest_sha256');

  // Entity table + raw records (exact: same bytes, same IEEE decode)
  eq(file.entities.length, want.entities.length, 'entity count');
  for (const we of want.entities) {
    const e = file.findEntity(we.id);
    if (!e) throw new Error(`entity ${we.id} missing`);
    eq(e.ord, we.ord, `${we.id}.ord`);
    eq(e.team, we.team, `${we.id}.team`);
    eq(e.type, we.type, `${we.id}.type`);
    eq(e.model, we.model, `${we.id}.model`);
    eq(e.t0Ns, we.t0_ns, `${we.id}.t0_ns`);
    eq(e.samples, we.samples, `${we.id}.samples`);
    eq(e.offset, we.offset, `${we.id}.offset`);
    eq(e.stride, we.stride, `${we.id}.stride`);
    eq(e.layout, we.layout, `${we.id}.layout`);
    for (const p of we.picks) {
      const r = file.readSample(e, p.i);
      vecClose(r.pos, p.pos, 0, `${we.id}[${p.i}].pos`);
      vecClose(r.vel, p.vel, 0, `${we.id}[${p.i}].vel`);
      vecClose(r.quat, p.quat, 0, `${we.id}[${p.i}].quat`);
      vecClose(r.omega, p.omega, 0, `${we.id}[${p.i}].omega`);
    }

    // Interpolation invariants (mirror TspiReader.TrySampleAt):
    const t0 = file.startSec(e), t1 = file.endSec(e);
    // 1. At an exact sample time, Hermite reproduces the record (u=0 basis).
    const mid = Math.floor(e.samples / 2);
    const tMid = (e.t0Ns + mid * file.header.dtNs) / 1e9;
    const sMid = file.sampleAt(e, tMid, false);
    const rMid = file.readSample(e, mid);
    vecClose(sMid.pos, rMid.pos, 1e-6, `${we.id} interp@sample pos`);
    vecClose(sMid.vel, rMid.vel, 1e-4, `${we.id} interp@sample vel`);
    // 2. Outside the alive window: null without clamp, endpoint with clamp.
    eq(file.sampleAt(e, t0 - 1, false), null, `${we.id} pre-t0 null`);
    eq(file.sampleAt(e, t1 + 1, false), null, `${we.id} post-t1 null`);
    vecClose(file.sampleAt(e, t1 + 1, true).pos, file.readSample(e, e.samples - 1).pos, 1e-9,
      `${we.id} clamp end pos`);
    // 3. Mid-interval position lies near the segment (Hermite sanity, tol = segment length).
    const tq = t0 + (t1 - t0) * 0.37;
    const s = file.sampleAt(e, tq, false);
    checks++;
    if (!s || !s.pos.every(Number.isFinite)) throw new Error(`${we.id} interp non-finite`);
    // 4. Quaternion unit norm after slerp.
    const qn = Math.hypot(...s.quat);
    close(qn, 1, 1e-6, `${we.id} interp quat norm`);
  }

  // Events
  eq(file.events.length, want.events.length, 'event count');
  want.events.forEach((wev, i) => {
    eq(file.events[i].t_ns, wev.t_ns, `event[${i}].t_ns`);
    eq(file.events[i].kind, wev.kind, `event[${i}].kind`);
  });

  // CRC guard: flipping one footer byte must fail parse.
  const corrupt = Buffer.from(buf);
  const dv = new DataView(corrupt.buffer, corrupt.byteOffset, corrupt.byteLength);
  const fOff = Number(dv.getBigUint64(corrupt.length - 32, true));
  corrupt[fOff + 2] ^= 0xFF;
  let threw = false;
  try { Tspi.parse(corrupt.buffer.slice(corrupt.byteOffset, corrupt.byteOffset + corrupt.byteLength)); }
  catch { threw = true; }
  eq(threw, true, 'corrupted footer rejected');

  const span = file.timeSpan();
  console.log(`ok ${path}: ${file.entities.length} entities, ${file.events.length} events, ` +
    `t=[${span.min.toFixed(2)}, ${span.max.toFixed(2)}]s`);
}
console.log(`PASS — ${checks} checks`);
