/* .tspi v1 reader — vanilla JS port of src/Tspi.Core/Runtime/IO/TspiReader.cs.
 *
 * Format (little-endian; see docs/FORMAT.md):
 *   [header 128 B] [entity blocks] [footer JSON] [trailer 32 B] (appends repeat)
 * The 32-byte trailer at EOF locates the newest JSON footer; the footer's entity
 * table gives each entity's contiguous fixed-stride sample block.
 *
 * Classic script (no modules) so index.html works from file://; also loadable
 * from Node for tests via module.exports at the bottom.
 */
(function () {
  'use strict';

  var HEADER_SIZE = 128;
  var TRAILER_SIZE = 32;
  var LAYOUT_6DOF_V1 = 1;
  var STRIDE_6DOF_V1 = 64;

  // i64/u64 arrive as BigInt; sim-relative times and file offsets fit in a
  // double, so convert eagerly and fail loudly rather than leak BigInt around.
  // (epoch_unix_ns is the exception — absolute ns overflow 2^53, kept BigInt.)
  function toNum(big, what) {
    var n = Number(big);
    if (!Number.isSafeInteger(n)) throw new Error('.tspi: ' + what + ' exceeds 2^53 (' + big + ')');
    return n;
  }

  var CRC_TABLE = (function () {
    var t = new Uint32Array(256);
    for (var i = 0; i < 256; i++) {
      var c = i;
      for (var k = 0; k < 8; k++) c = (c & 1) ? (0xEDB88320 ^ (c >>> 1)) : (c >>> 1);
      t[i] = c >>> 0;
    }
    return t;
  })();

  function crc32(bytes) {
    var c = 0xFFFFFFFF;
    for (var i = 0; i < bytes.length; i++) c = CRC_TABLE[(c ^ bytes[i]) & 0xFF] ^ (c >>> 8);
    return (c ^ 0xFFFFFFFF) >>> 0;
  }

  function ascii(bytes, off, len) {
    var s = '';
    for (var i = 0; i < len; i++) s += String.fromCharCode(bytes[off + i]);
    return s;
  }

  function hex(bytes, off, len) {
    var s = '';
    for (var i = 0; i < len; i++) s += bytes[off + i].toString(16).padStart(2, '0');
    return s;
  }

  /** Parse a complete .tspi file from an ArrayBuffer. Throws on any structural error. */
  function parse(buffer, name) {
    var bytes = new Uint8Array(buffer);
    var dv = new DataView(buffer);
    if (bytes.length < HEADER_SIZE + TRAILER_SIZE) throw new Error('.tspi: file too small');
    if (ascii(bytes, 0, 4) !== 'TSPI') throw new Error('.tspi: bad file magic');

    var header = {
      version: dv.getUint32(4, true),
      flags: dv.getUint32(8, true),
      dtNs: toNum(dv.getBigUint64(16, true), 'dt_ns'),
      epochUnixNs: dv.getBigInt64(24, true),
      epochUnixMs: Number(dv.getBigInt64(24, true) / 1000000n),
      originLatDeg: dv.getFloat64(32, true),
      originLonDeg: dv.getFloat64(40, true),
      originAltM: dv.getFloat64(48, true),
      manifestSha256: hex(bytes, 56, 32),
    };
    if (header.version !== 1) throw new Error('.tspi: unsupported format version ' + header.version);
    if (header.dtNs <= 0) throw new Error('.tspi: dt_ns must be positive');

    var tOff = bytes.length - TRAILER_SIZE;
    if (ascii(bytes, tOff + 24, 8) !== 'TSPIFTR1')
      throw new Error(".tspi: no valid trailer at EOF (torn write? run 'tspi recover')");
    var footerOffset = toNum(dv.getBigUint64(tOff, true), 'footer_offset');
    var footerLen = toNum(dv.getBigUint64(tOff + 8, true), 'footer_len');
    var footerCrc = dv.getUint32(tOff + 16, true);
    if (footerOffset + footerLen > bytes.length) throw new Error('.tspi: footer out of file bounds');
    var footerBytes = bytes.subarray(footerOffset, footerOffset + footerLen);
    if (crc32(footerBytes) !== footerCrc) throw new Error('.tspi: footer CRC mismatch');
    var footer = JSON.parse(new TextDecoder('utf-8').decode(footerBytes));

    var entities = (footer.entities || []).map(function (e) {
      return {
        ord: e.ord, id: e.id, team: e.team, type: e.type, model: e.model,
        parent: e.parent == null ? null : e.parent,
        t0Ns: e.t0_ns, samples: e.samples,
        offset: e.offset, stride: e.stride, layout: e.layout,
      };
    });
    entities.forEach(function (e) {
      if (e.layout !== LAYOUT_6DOF_V1) return; // unknown layouts are legal; unsampleable
      if (e.stride < STRIDE_6DOF_V1) throw new Error(".tspi: entity '" + e.id + "' stride below layout-1 prefix size");
      var end = e.offset + e.samples * e.stride;
      if (e.offset < HEADER_SIZE || end > bytes.length)
        throw new Error(".tspi: entity '" + e.id + "' block out of file bounds");
    });

    return new TspiFile(name || '', dv, header, footer, entities);
  }

  function TspiFile(name, dv, header, footer, entities) {
    this.name = name;
    this._dv = dv;
    this.header = header;
    this.footer = footer;
    this.entities = entities;
    this.events = footer.events || [];
    this.provenance = footer.provenance || [];
    this.environment = footer.environment || null;
    this.dtSec = header.dtNs / 1e9;
  }

  TspiFile.prototype.findEntity = function (id) {
    for (var i = 0; i < this.entities.length; i++)
      if (this.entities[i].id === id) return this.entities[i];
    return null;
  };

  TspiFile.prototype.startSec = function (e) { return e.t0Ns / 1e9; };

  TspiFile.prototype.endSec = function (e) {
    return (e.t0Ns + (e.samples - 1) * this.header.dtNs) / 1e9;
  };

  /** Raw record i of an entity: {pos:[3] f64, vel:[3] f32, quat:[4] wxyz f32, omega:[3] f32}. */
  TspiFile.prototype.readSample = function (e, i) {
    if (i < 0 || i >= e.samples) throw new Error('.tspi: sample index out of range');
    var dv = this._dv;
    var o = e.offset + i * e.stride;
    return {
      pos: [dv.getFloat64(o, true), dv.getFloat64(o + 8, true), dv.getFloat64(o + 16, true)],
      vel: [dv.getFloat32(o + 24, true), dv.getFloat32(o + 28, true), dv.getFloat32(o + 32, true)],
      quat: [dv.getFloat32(o + 36, true), dv.getFloat32(o + 40, true), dv.getFloat32(o + 44, true), dv.getFloat32(o + 48, true)],
      omega: [dv.getFloat32(o + 52, true), dv.getFloat32(o + 56, true), dv.getFloat32(o + 60, true)],
    };
  };

  function normQ(q) {
    var m = Math.sqrt(q[0] * q[0] + q[1] * q[1] + q[2] * q[2] + q[3] * q[3]);
    if (m < 1e-12) return [1, 0, 0, 0];
    return [q[0] / m, q[1] / m, q[2] / m, q[3] / m];
  }

  // Shortest-path slerp with nlerp fallback — mirrors QuatD.Slerp.
  function slerp(a, b, t) {
    var dot = a[0] * b[0] + a[1] * b[1] + a[2] * b[2] + a[3] * b[3];
    if (dot < 0) { b = [-b[0], -b[1], -b[2], -b[3]]; dot = -dot; }
    if (dot > 0.9995) {
      return normQ([
        a[0] + t * (b[0] - a[0]), a[1] + t * (b[1] - a[1]),
        a[2] + t * (b[2] - a[2]), a[3] + t * (b[3] - a[3]),
      ]);
    }
    var th0 = Math.acos(dot), th = th0 * t, s0 = Math.sin(th0);
    var sA = Math.sin(th0 - th) / s0, sB = Math.sin(th) / s0;
    return [
      sA * a[0] + sB * b[0], sA * a[1] + sB * b[1],
      sA * a[2] + sB * b[2], sA * a[3] + sB * b[3],
    ];
  }

  /**
   * Interpolated state at tSec (seconds since the header epoch), or null when t is
   * outside the entity's alive window and clamp is false. Cubic Hermite position
   * (stored velocities as tangents), Hermite-derivative velocity, slerped attitude,
   * lerped body rates — identical to TspiReader.TrySampleAt.
   */
  TspiFile.prototype.sampleAt = function (e, tSec, clamp) {
    if (e.samples <= 0 || e.layout !== LAYOUT_6DOF_V1) return null;
    var t0 = this.startSec(e), t1 = this.endSec(e);
    if (tSec < t0 || tSec > t1) {
      if (!clamp) return null;
      tSec = tSec < t0 ? t0 : t1;
    }
    if (e.samples === 1) {
      var only = this.readSample(e, 0);
      return { pos: only.pos, vel: only.vel, quat: normQ(only.quat), omega: only.omega };
    }
    var dt = this.dtSec;
    var u = (tSec - t0) / dt;
    var i = Math.floor(u);
    if (i < 0) i = 0;
    if (i > e.samples - 2) i = e.samples - 2;
    u -= i;

    var a = this.readSample(e, i);
    var b = this.readSample(e, i + 1);

    var h00 = (2 * u - 3) * u * u + 1;
    var h10 = ((u - 2) * u + 1) * u;
    var h01 = (3 - 2 * u) * u * u;
    var h11 = (u - 1) * u * u;
    var g00 = 6 * u * u - 6 * u;
    var g10 = 3 * u * u - 4 * u + 1;
    var g01 = -6 * u * u + 6 * u;
    var g11 = 3 * u * u - 2 * u;

    var pos = [0, 0, 0], vel = [0, 0, 0];
    for (var k = 0; k < 3; k++) {
      pos[k] = h00 * a.pos[k] + h10 * dt * a.vel[k] + h01 * b.pos[k] + h11 * dt * b.vel[k];
      vel[k] = (g00 / dt) * a.pos[k] + g10 * a.vel[k] + (g01 / dt) * b.pos[k] + g11 * b.vel[k];
    }
    return {
      pos: pos,
      vel: vel,
      quat: slerp(normQ(a.quat), normQ(b.quat), u),
      omega: [
        a.omega[0] + u * (b.omega[0] - a.omega[0]),
        a.omega[1] + u * (b.omega[1] - a.omega[1]),
        a.omega[2] + u * (b.omega[2] - a.omega[2]),
      ],
    };
  };

  /** Time span [min start, max end] across all sampleable entities. */
  TspiFile.prototype.timeSpan = function () {
    var min = Infinity, max = -Infinity;
    for (var i = 0; i < this.entities.length; i++) {
      var e = this.entities[i];
      if (e.samples <= 0) continue;
      min = Math.min(min, this.startSec(e));
      max = Math.max(max, this.endSec(e));
    }
    return { min: min, max: max };
  };

  var api = { parse: parse, crc32: crc32, slerp: slerp, LAYOUT_6DOF_V1: LAYOUT_6DOF_V1 };
  if (typeof module !== 'undefined' && module.exports) module.exports = api;
  else this.Tspi = api;
}).call(this);
