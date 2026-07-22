"""The tspi-dcv/1 view: the fly-out in launch-centred DCV coordinates.

One record per launch event, derived from tspi-engagement/1 plus the munition's
own track (which no other view carries). Like the engagement view this is a
*view*, not a format — rebuilt from the .tspi runs on every call.

Frame (per launch): origin at the munition's launch position; axes
    D  downrange   horizontal unit vector from launch toward the target's
                   position at launch; degenerate-bearing fallback chain
                   (frame.downrange_ref records which won): launch->target
                   bearing, else launch-velocity heading, else the target's
                   horizontal velocity direction (vertical ship launch with
                   the target directly overhead)
    C  crossrange  positive to the right of the shot line viewed from above
    V  vertical    positive up (height above the launch point)

(D, C, V) is a left-handed coordinate triple — convenient for plotting, not a
dynamics frame — so attitude quaternions are deliberately left body->NED.
`frame.dcv_from_ned` holds the orthogonal row matrix [D̂; Ĉ; V̂] in NED:
p_dcv = R @ (p_ned - origin), and p_ned = origin + R.T @ p_dcv.

Record shape (meters / m/s / seconds since the header epoch):

    meta      source, source_sha256, origin_lla, epoch_unix_ns
    frame     origin_ned_m, dcv_from_ned [3,3], downrange_ref
    launch    t_s, munition_id, launcher_id, target_id
    munition  dt_s, t0_s, t_s [N], pos_dcv_m [N,3], vel_dcv_mps [N,3],
              att_wxyz [N,4] (body->NED), omega_body_rps [N,3]
    target    same fields as munition
              — both windowed to the fly-out: [launch, min(terminal, launch + window_s)]
    outcome   terminal, t_terminal_s, miss_m (as tspi-engagement/1)
"""

from __future__ import annotations

import math
from dataclasses import dataclass
from types import SimpleNamespace

import numpy as np

from .engagements import engagements
from .reader import TspiFile

# Below this horizontal separation the shot line has no bearing to define.
_HORIZONTAL_EPS_M = 1e-6


@dataclass
class DcvFlyout:
    meta: SimpleNamespace
    frame: SimpleNamespace
    launch: SimpleNamespace
    munition: SimpleNamespace
    target: SimpleNamespace
    outcome: SimpleNamespace

    schema = "tspi-dcv/1"


def _dcv_basis(launch_pos_ned, target_pos_ned, launch_vel_ned, target_vel_ned):
    """NED->DCV row matrix [D̂; Ĉ; V̂] and which reference fixed the bearing."""
    for ref, vec in (("target", target_pos_ned - launch_pos_ned),
                     ("launch_velocity", launch_vel_ned),
                     ("target_velocity", target_vel_ned)):
        h = math.hypot(float(vec[0]), float(vec[1]))
        if h > _HORIZONTAL_EPS_M:
            dn, de = float(vec[0]) / h, float(vec[1]) / h
            return np.array([[dn, de, 0.0],
                             [-de, dn, 0.0],
                             [0.0, 0.0, -1.0]]), ref
    raise ValueError(
        "degenerate DCV frame: launch->target line, launch velocity, and target "
        "velocity are all vertical")


def dcv_flyouts(paths, window_s: float | None = 100.0) -> list[DcvFlyout]:
    """DCV fly-out records for every launch event across one or many .tspi files.

    `paths` / `window_s` behave as in `engagements` (None = full recorded
    tracks). Positions and velocities are converted copies; attitude and body
    rates are zero-copy views in their native frames.
    """
    out: list[DcvFlyout] = []
    files: dict[str, TspiFile] = {}
    for e in engagements(paths, window_s):
        f = files.setdefault(e.meta.source, TspiFile(e.meta.source))
        origin = e.launch.pos_ned_m
        rot, ref = _dcv_basis(origin, e.launch.target_pos_ned_m,
                              e.launch.vel_ned_mps, e.launch.target_vel_ned_mps)

        def track(pos_ned, vel_ned, att, omega, t_s, t0_s, dt_s):
            return SimpleNamespace(
                dt_s=dt_s, t0_s=t0_s, t_s=t_s,
                pos_dcv_m=(np.asarray(pos_ned, dtype=np.float64) - origin) @ rot.T,
                vel_dcv_mps=np.asarray(vel_ned, dtype=np.float64) @ rot.T,
                att_wxyz=att, omega_body_rps=omega,
            )

        # The munition's whole track is the fly-out (born at launch, recorded to
        # terminal); apply the same cap the engagement view puts on the target.
        mun = f.entity(e.launch.munition_id)
        hi = mun.samples
        if window_s is not None:
            t_end = e.launch.t_s + window_s
            if e.outcome.terminal is not None:
                t_end = min(e.outcome.t_terminal_s, t_end)
            hi = min(mun.samples, int(math.ceil((t_end - mun.t0_s) / f.dt_s - 1e-9)) + 1)
        arr, times = f.samples(mun.id)[:hi], f.times(mun.id)[:hi]

        out.append(DcvFlyout(
            meta=e.meta,
            frame=SimpleNamespace(origin_ned_m=origin, dcv_from_ned=rot, downrange_ref=ref),
            launch=SimpleNamespace(
                t_s=e.launch.t_s, munition_id=e.launch.munition_id,
                launcher_id=e.launch.launcher_id, target_id=e.launch.target_id),
            munition=track(arr["pos"], arr["vel"], arr["quat"], arr["omega"],
                           times, mun.t0_s, f.dt_s),
            target=track(e.target.pos_ned_m, e.target.vel_ned_mps,
                         e.target.att_wxyz, e.target.omega_body_rps,
                         e.target.t_s, e.target.t0_s, e.target.dt_s),
            outcome=e.outcome,
        ))
    return out


def save_mat(path: str, flys: list[DcvFlyout]) -> None:
    """One-file shipment for MATLAB: a 1xN struct array `flyouts`.

    A shipment, not a store — regenerate from the .tspi runs when in doubt
    (mirrors engagements.save_mat). Requires scipy. Import as
    `from tspi_py.dcv import save_mat`; loads as F.flyouts(k).munition.pos_dcv_m etc."""
    from scipy.io import savemat

    def ns(x):
        return {k: ("" if v is None else v) for k, v in vars(x).items()}

    savemat(path, {
        "schema": DcvFlyout.schema,
        "flyouts": np.array(
            [{"meta": ns(f.meta), "frame": ns(f.frame), "launch": ns(f.launch),
              "munition": ns(f.munition), "target": ns(f.target), "outcome": ns(f.outcome)}
             for f in flys]),
    }, do_compression=True)
