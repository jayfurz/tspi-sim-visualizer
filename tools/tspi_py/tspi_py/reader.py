"""Memory-mapped .tspi reader.

Format (little-endian; see docs/FORMAT.md):
  [header 128 B] [entity blocks] [footer JSON] [trailer 32 B] (appends repeat blocks/footer/trailer)

The 32-byte trailer at EOF locates the newest JSON footer; the footer's entity
table gives each entity's contiguous fixed-stride sample block. Samples are
mapped zero-copy as a numpy structured array.
"""

from __future__ import annotations

import json
import struct
import zlib
from dataclasses import dataclass, field

import numpy as np

FILE_MAGIC = b"TSPI"
TRAILER_MAGIC = b"TSPIFTR1"
HEADER_SIZE = 128
TRAILER_SIZE = 32
LAYOUT_6DOF_V1 = 1

# Record layout 1 (stride 64): pos f64x3 | vel f32x3 | quat wxyz f32x4 | omega f32x3.
RECORD_DTYPE = np.dtype(
    {
        "names": ["pos", "vel", "quat", "omega"],
        "formats": [("<f8", (3,)), ("<f4", (3,)), ("<f4", (4,)), ("<f4", (3,))],
        "offsets": [0, 24, 36, 52],
        "itemsize": 64,
    }
)


@dataclass
class Entity:
    ord: int
    id: str
    team: str
    type: str
    model: str
    parent: int | None
    t0_ns: int
    samples: int
    offset: int
    stride: int
    layout: int

    @property
    def t0_s(self) -> float:
        return self.t0_ns / 1e9


@dataclass
class Event:
    t_ns: int
    kind: str
    src: int | None
    dst: int | None
    data: dict = field(default_factory=dict)

    @property
    def t_s(self) -> float:
        return self.t_ns / 1e9


class TspiFile:
    """Zero-copy access to a .tspi file. Usage:

    >>> f = TspiFile("run.tspi")
    >>> arr = f.samples("blue-01")          # numpy structured array (pos/vel/quat/omega)
    >>> t = f.times("blue-01")              # seconds since header epoch
    >>> f.events                            # decoded footer event log
    """

    def __init__(self, path: str):
        self.path = path
        self._mm = np.memmap(path, dtype=np.uint8, mode="r")
        size = self._mm.size
        if size < HEADER_SIZE + TRAILER_SIZE:
            raise ValueError(f"{path}: too small to be a .tspi file")

        head = bytes(self._mm[:HEADER_SIZE])
        if head[:4] != FILE_MAGIC:
            raise ValueError(f"{path}: bad file magic")
        (self.version, self.flags) = struct.unpack_from("<II", head, 4)
        (self.dt_ns,) = struct.unpack_from("<Q", head, 16)
        (self.epoch_unix_ns,) = struct.unpack_from("<q", head, 24)
        (self.origin_lat_deg, self.origin_lon_deg, self.origin_alt_m) = struct.unpack_from("<3d", head, 32)
        self.manifest_sha256 = head[56:88].hex()
        if self.version != 1:
            raise ValueError(f"{path}: unsupported format version {self.version}")

        trailer = bytes(self._mm[size - TRAILER_SIZE:])
        f_off, f_len, f_crc, _res = struct.unpack_from("<QQII", trailer, 0)
        if trailer[24:32] != TRAILER_MAGIC:
            raise ValueError(f"{path}: no valid trailer at EOF (torn write? run 'tspi recover')")
        footer_bytes = bytes(self._mm[f_off:f_off + f_len])
        if zlib.crc32(footer_bytes) & 0xFFFFFFFF != f_crc:
            raise ValueError(f"{path}: footer CRC mismatch")
        self.footer = json.loads(footer_bytes)
        self.footer_offset = f_off

        self.entities: dict[str, Entity] = {}
        for e in self.footer["entities"]:
            ent = Entity(
                ord=e["ord"], id=e["id"], team=e["team"], type=e["type"], model=e["model"],
                parent=e.get("parent"), t0_ns=e["t0_ns"], samples=e["samples"],
                offset=e["offset"], stride=e["stride"], layout=e["layout"],
            )
            self.entities[ent.id] = ent
        self.events = [
            Event(t_ns=v["t_ns"], kind=v["kind"], src=v.get("src"), dst=v.get("dst"),
                  data=v.get("data") or {})
            for v in self.footer.get("events", [])
        ]
        self.provenance = self.footer.get("provenance", [])
        # Environment (atmosphere + wind) the producing scenario used; carried across appends.
        self.environment = self.footer.get("environment")

    @property
    def dt_s(self) -> float:
        return self.dt_ns / 1e9

    def entity(self, id_or_ord: str | int) -> Entity:
        if isinstance(id_or_ord, str):
            return self.entities[id_or_ord]
        for e in self.entities.values():
            if e.ord == id_or_ord:
                return e
        raise KeyError(id_or_ord)

    def samples(self, id_or_ord: str | int) -> np.ndarray:
        """Structured array view (zero-copy) of one entity's records."""
        e = self.entity(id_or_ord)
        if e.layout != LAYOUT_6DOF_V1:
            raise ValueError(f"entity '{e.id}' uses unknown layout {e.layout}")
        if e.stride == RECORD_DTYPE.itemsize:
            raw = self._mm[e.offset: e.offset + e.samples * e.stride]
            return raw.view(RECORD_DTYPE)
        # Forward-compat: wider layouts keep the 64-byte prefix; view with padding.
        padded = np.dtype({
            "names": list(RECORD_DTYPE.names),
            "formats": [RECORD_DTYPE.fields[n][0] for n in RECORD_DTYPE.names],
            "offsets": [RECORD_DTYPE.fields[n][1] for n in RECORD_DTYPE.names],
            "itemsize": e.stride,
        })
        raw = self._mm[e.offset: e.offset + e.samples * e.stride]
        return raw.view(padded)

    def times(self, id_or_ord: str | int) -> np.ndarray:
        """Sample times in seconds since the header epoch (implicit: t0 + i*dt)."""
        e = self.entity(id_or_ord)
        return (e.t0_ns + np.arange(e.samples, dtype=np.int64) * self.dt_ns) / 1e9

    def to_arrow(self, id_or_ord: str | int):
        """One entity as a pyarrow Table (requires the 'arrow' extra)."""
        import pyarrow as pa

        e = self.entity(id_or_ord)
        arr = self.samples(e.id)
        t = self.times(e.id)
        cols = {
            "t_s": t,
            "pos_n": arr["pos"][:, 0], "pos_e": arr["pos"][:, 1], "pos_d": arr["pos"][:, 2],
            "vel_n": arr["vel"][:, 0], "vel_e": arr["vel"][:, 1], "vel_d": arr["vel"][:, 2],
            "qw": arr["quat"][:, 0], "qx": arr["quat"][:, 1],
            "qy": arr["quat"][:, 2], "qz": arr["quat"][:, 3],
            "wx": arr["omega"][:, 0], "wy": arr["omega"][:, 1], "wz": arr["omega"][:, 2],
        }
        return pa.table({k: pa.array(np.ascontiguousarray(v)) for k, v in cols.items()})

    def __repr__(self) -> str:
        return (f"TspiFile({self.path!r}, {len(self.entities)} entities, "
                f"dt={self.dt_s * 1000:.3g} ms, events={len(self.events)})")
