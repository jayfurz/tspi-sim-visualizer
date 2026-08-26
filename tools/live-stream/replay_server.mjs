#!/usr/bin/env node
// Replay a .tspi file to the web viewer as a *live* stream — a stand-in producer
// for a real simulator, and the test fixture for the viewer's live path.
//
//   node tools/live-stream/replay_server.mjs runs/ship-to-air.tspi [--port 8787] [--rate 1]
//   open http://localhost:8787/?ws=ws://localhost:8787/stream
//
// Zero dependencies: the static file server, the RFC6455 handshake and the frame
// writer are all hand-rolled (same delivery constraint as the viewer itself).
// Speaks the protocol in tools/live-stream/PROTOCOL.md.
import { createServer } from 'node:http';
import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import { extname, join, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';

const require = createRequire(import.meta.url);
const here = dirname(fileURLToPath(import.meta.url));
const VIEWER_DIR = resolve(here, '../../web/viewer');
const Tspi = require(join(VIEWER_DIR, 'tspi.js'));

const argv = process.argv.slice(2);
const flag = (name, dflt) => {
  const i = argv.indexOf('--' + name);
  return i >= 0 && argv[i + 1] !== undefined ? argv[i + 1] : dflt;
};
const positional = argv.filter((a, i) => !a.startsWith('--') && !(i > 0 && argv[i - 1].startsWith('--')));
const path = positional[0];
if (!path) {
  console.error('usage: replay_server.mjs <file.tspi> [--port 8787] [--rate 1] [--tick-ms 50]');
  process.exit(2);
}
const PORT = Number(flag('port', 8787));
const RATE = Number(flag('rate', 1));
const TICK_MS = Number(flag('tick-ms', 50));

const raw = readFileSync(path);
const file = Tspi.parse(raw.buffer.slice(raw.byteOffset, raw.byteOffset + raw.byteLength), path);
const span = file.timeSpan();
const sampleable = file.entities.filter((e) => e.layout === Tspi.LAYOUT_6DOF_V1 && e.samples > 0);
console.log(`replaying ${path}: ${sampleable.length} entities, ` +
  `t=${span.min.toFixed(2)}..${span.max.toFixed(2)}s, dt=${(file.dtSec * 1000).toFixed(1)}ms, rate ${RATE}x`);

// ---------- RFC6455: just enough for one-way server pushes ----------
const GUID = '258EAFA5-E914-47DA-95CA-C5AB0DC85B11';

function frame(opcode, payload) {
  const n = payload.length;
  const head = n < 126 ? Buffer.alloc(2)
    : n < 65536 ? Buffer.alloc(4) : Buffer.alloc(10);
  head[0] = 0x80 | opcode;                      // FIN + opcode
  if (n < 126) head[1] = n;
  else if (n < 65536) { head[1] = 126; head.writeUInt16BE(n, 2); }
  else { head[1] = 127; head.writeBigUInt64BE(BigInt(n), 2); }
  return Buffer.concat([head, payload]);        // server->client is never masked
}
const sendText = (sock, obj) => sock.write(frame(0x1, Buffer.from(JSON.stringify(obj), 'utf8')));
const sendBin = (sock, buf) => sock.write(frame(0x2, buf));

// ---------- record + batch encoding (layout 1, 64 B) ----------
function writeRecord(buf, off, r) {
  buf.writeDoubleLE(r.pos[0], off); buf.writeDoubleLE(r.pos[1], off + 8); buf.writeDoubleLE(r.pos[2], off + 16);
  buf.writeFloatLE(r.vel[0], off + 24); buf.writeFloatLE(r.vel[1], off + 28); buf.writeFloatLE(r.vel[2], off + 32);
  buf.writeFloatLE(r.quat[0], off + 36); buf.writeFloatLE(r.quat[1], off + 40);
  buf.writeFloatLE(r.quat[2], off + 44); buf.writeFloatLE(r.quat[3], off + 48);
  buf.writeFloatLE(r.omega[0], off + 52); buf.writeFloatLE(r.omega[1], off + 56); buf.writeFloatLE(r.omega[2], off + 60);
}
function batch(items) {                          // [u32 count]([u32 ord][u32 idx][64 B])*
  const buf = Buffer.alloc(4 + items.length * 72);
  buf.writeUInt32LE(items.length, 0);
  items.forEach(({ ord, index, rec }, k) => {
    const b = 4 + k * 72;
    buf.writeUInt32LE(ord, b);
    buf.writeUInt32LE(index, b + 4);
    writeRecord(buf, b + 8, rec);
  });
  return buf;
}

function helloFor() {
  return {
    type: 'hello', protocol: Tspi.LIVE_PROTOCOL, name: `${path} (replay)`,
    dt_ns: file.header.dtNs,
    epoch_unix_ns: String(file.header.epochUnixNs),
    origin: {
      lat_deg: file.header.originLatDeg, lon_deg: file.header.originLonDeg,
      alt_m: file.header.originAltM,
    },
    dynamics: `replay of ${path}`,
    // Entities alive at t0 only; the rest are announced as they spawn, which is
    // what a real producer does for munitions.
    entities: sampleable.filter((e) => file.startSec(e) <= span.min + 1e-9).map(entityDesc),
  };
}
const entityDesc = (e) => ({
  ord: e.ord, id: e.id, team: e.team, type: e.type, model: e.model,
  parent: e.parent, t0_ns: e.t0Ns, layout: e.layout,
});

// ---------- one replay session per connected viewer ----------
function startSession(sock) {
  let t = span.min;                              // sim seconds already sent
  const sent = new Map(sampleable.map((e) => [e.ord, 0]));
  const announced = new Set();
  let evIdx = 0;
  sendText(sock, helloFor());
  helloFor().entities.forEach((d) => announced.add(d.ord));

  const timer = setInterval(() => {
    if (sock.destroyed) return clearInterval(timer);
    t += (TICK_MS / 1000) * RATE;
    const items = [];
    for (const e of sampleable) {
      const start = file.startSec(e);
      let i = sent.get(e.ord);
      const due = Math.min(e.samples, Math.floor((t - start) / file.dtSec) + 1);
      if (due > i && !announced.has(e.ord)) { sendText(sock, { type: 'entity', entity: entityDesc(e) }); announced.add(e.ord); }
      for (; i < due; i++) items.push({ ord: e.ord, index: i, rec: file.readSample(e, i) });
      sent.set(e.ord, i);
    }
    if (items.length) sendBin(sock, batch(items));
    while (evIdx < file.events.length && file.events[evIdx].t_ns / 1e9 <= t) {
      const ev = file.events[evIdx++];
      sendText(sock, { type: 'event', t_ns: ev.t_ns, kind: ev.kind, src: ev.src, dst: ev.dst, data: ev.data });
    }
    if (t >= span.max) {
      sendText(sock, { type: 'end' });
      clearInterval(timer);
      console.log('replay complete');
    }
  }, TICK_MS);
  sock.on('close', () => clearInterval(timer));
  sock.on('error', () => clearInterval(timer));
}

// ---------- HTTP: static viewer + websocket upgrade ----------
const MIME = { '.html': 'text/html', '.js': 'text/javascript', '.css': 'text/css', '.tspi': 'application/octet-stream' };
const server = createServer((req, res) => {
  const url = new URL(req.url, 'http://localhost');
  const rel = url.pathname === '/' ? '/index.html' : url.pathname;
  const full = resolve(VIEWER_DIR, '.' + rel);
  if (!full.startsWith(VIEWER_DIR)) { res.writeHead(403).end('forbidden'); return; }
  try {
    const body = readFileSync(full);
    res.writeHead(200, { 'content-type': MIME[extname(full)] || 'application/octet-stream' });
    res.end(body);
  } catch {
    res.writeHead(404).end('not found');
  }
});

server.on('upgrade', (req, sock) => {
  const key = req.headers['sec-websocket-key'];
  if (!key || new URL(req.url, 'http://x').pathname !== '/stream') { sock.destroy(); return; }
  sock.write('HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n' +
    'Sec-WebSocket-Accept: ' + createHash('sha1').update(key + GUID).digest('base64') + '\r\n\r\n');
  sock.setNoDelay(true);
  sock.on('data', () => { /* client frames (pings/close) are ignored */ });
  console.log('viewer connected');
  startSession(sock);
});

server.listen(PORT, () => {
  console.log(`viewer:  http://localhost:${PORT}/?ws=ws://localhost:${PORT}/stream`);
  console.log(`stream:  ws://localhost:${PORT}/stream`);
});
