"""The tspi-engagement/1 view: one record per launch event.

This is a *view*, not a format — engagement records are rebuilt in memory from
.tspi run files on every call, so the run files stay the single source of truth
(nothing to re-export, appends just show up). Serialize the returned list only
as a shipment to someone without access to the runs (see `save_mat`).

Record shape (all NED meters / m/s / seconds since the header epoch, quaternions
wxyz Hamilton body->NED — docs/CONVENTIONS.md):

    meta     source, source_sha256 (manifest hash), origin_lla, epoch_unix_ns
    launch   t_s, munition_id, launcher_id, target_id,
             pos_ned_m, vel_ned_mps, att_wxyz          (munition at launch)
             target_pos_ned_m, target_vel_ned_mps      (target at launch)
    target   dt_s, t0_s, t_s [N], pos_ned_m [N,3], vel_ned_mps [N,3],
             att_wxyz [N,4], omega_body_rps [N,3]      (arrays are zero-copy views)
             — windowed to the fly-out: [launch, min(terminal, launch + window_s)]
    outcome  terminal ('intercept'|'cpa'|'ground_impact'|'expire'|None),
             t_terminal_s, miss_m (NaN when not applicable)
"""

from __future__ import annotations

import math
from dataclasses import dataclass
from types import SimpleNamespace

import numpy as np

from .reader import TspiFile

# Event kinds that end a munition's flight, in the precedence used when a run
# carries several for the same munition (cpa is informational next to intercept).
_TERMINAL_KINDS = ("intercept", "ground_impact", "expire", "cpa")


@dataclass
class Engagement:
    meta: SimpleNamespace
    launch: SimpleNamespace
    target: SimpleNamespace
    outcome: SimpleNamespace

    schema = "tspi-engagement/1"


def _state_at_grid(f: TspiFile, ent, t_ns: int):
    """Record of `ent` at an exact grid time (raises if t is off-grid or outside)."""
    idx, rem = divmod(t_ns - ent.t0_ns, f.dt_ns)
    if rem != 0 or not (0 <= idx < ent.samples):
        raise ValueError(f"t={t_ns}ns not on {ent.id}'s sample grid")
    return f.samples(ent.id)[idx]


def engagements(paths, window_s: float | None = 100.0) -> list[Engagement]:
    """Engagement records for every launch event across one or many .tspi files.

    `paths`: a path, or an iterable of paths (order preserved).
    `window_s`: the target track is windowed to the fly-out — from launch to the
    munition's terminal event, capped at launch + window_s (default 100 s, sized to
    cover any max_flight_time_s in the stock models). Pass None for the full
    recorded track. Slices are zero-copy either way.
    """
    if isinstance(paths, (str, bytes)):
        paths = [paths]
    out: list[Engagement] = []
    for path in paths:
        f = TspiFile(path)
        by_ord = {e.ord: e for e in f.entities.values()}
        for ev in f.events:
            if ev.kind != "launch":
                continue
            mun = by_ord[ev.src]
            tgt = by_ord[ev.dst]

            # Launch == munition birth: its first record is the launch state.
            if mun.t0_ns != ev.t_ns:
                raise ValueError(
                    f"{path}: launch event at {ev.t_ns}ns but {mun.id} t0={mun.t0_ns}ns")
            m0 = f.samples(mun.id)[0]
            tgt_at = _state_at_grid(f, tgt, ev.t_ns)

            terminal, t_term, miss = None, math.nan, math.nan
            for kind in _TERMINAL_KINDS:
                for e2 in f.events:
                    if e2.kind == kind and e2.src == mun.ord:
                        terminal, t_term = kind, e2.t_s
                        miss = float(e2.data.get("miss_m", math.nan))
                        break
                if terminal:
                    break

            arr = f.samples(tgt.id)
            times = f.times(tgt.id)
            lo, hi = 0, tgt.samples  # slice bounds into the target track
            if window_s is not None:
                t_end = ev.t_s + window_s
                if terminal is not None:
                    t_end = min(t_term, t_end)
                lo = max(0, int(math.floor((ev.t_s - tgt.t0_s) / f.dt_s)))
                hi = min(tgt.samples, int(math.ceil((t_end - tgt.t0_s) / f.dt_s - 1e-9)) + 1)
            arr, times = arr[lo:hi], times[lo:hi]
            out.append(Engagement(
                meta=SimpleNamespace(
                    source=path,
                    source_sha256=f.manifest_sha256,
                    origin_lla=(f.origin_lat_deg, f.origin_lon_deg, f.origin_alt_m),
                    epoch_unix_ns=f.epoch_unix_ns,
                ),
                launch=SimpleNamespace(
                    t_s=ev.t_s,
                    munition_id=mun.id, launcher_id=(by_ord[mun.parent].id
                                                     if mun.parent is not None else None),
                    target_id=tgt.id,
                    pos_ned_m=np.asarray(m0["pos"], dtype=np.float64),
                    vel_ned_mps=np.asarray(m0["vel"], dtype=np.float64),
                    att_wxyz=np.asarray(m0["quat"], dtype=np.float64),
                    target_pos_ned_m=np.asarray(tgt_at["pos"], dtype=np.float64),
                    target_vel_ned_mps=np.asarray(tgt_at["vel"], dtype=np.float64),
                ),
                target=SimpleNamespace(
                    dt_s=f.dt_s, t0_s=float(times[0]) if len(times) else tgt.t0_s,
                    t_s=times,
                    pos_ned_m=arr["pos"], vel_ned_mps=arr["vel"],
                    att_wxyz=arr["quat"], omega_body_rps=arr["omega"],
                ),
                outcome=SimpleNamespace(
                    terminal=terminal, t_terminal_s=t_term, miss_m=miss,
                ),
            ))
    return out


def save_mat(path: str, engs: list[Engagement]) -> None:
    """One-file shipment for MATLAB: a 1xN struct array `engagements`.

    A shipment, not a store — regenerate from the .tspi runs when in doubt.
    Requires scipy. Loads as E.engagements(k).launch.pos_ned_m etc."""
    from scipy.io import savemat

    def ns(x):
        return {k: ("" if v is None else v) for k, v in vars(x).items()}

    savemat(path, {
        "schema": Engagement.schema,
        "engagements": np.array(
            [{"meta": ns(e.meta), "launch": ns(e.launch),
              "target": ns(e.target), "outcome": ns(e.outcome)} for e in engs]),
    }, do_compression=True)
