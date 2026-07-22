"""tspi-dcv/1 view tests against the committed golden .tspi.

The golden dart launches from blue-01 at t=0.5 toward red-01 and expires
unguided; expectations below are derived from the file, not hardcoded."""

import math
import os

import numpy as np
import pytest

from tspi_py import TspiFile, dcv_flyouts

GOLDEN = os.path.join(os.path.dirname(__file__), "data", "golden-v1.tspi")


@pytest.fixture(scope="module")
def f() -> TspiFile:
    return TspiFile(GOLDEN)


@pytest.fixture(scope="module")
def fly(f):
    flys = dcv_flyouts(GOLDEN)
    assert len(flys) == 1
    return flys[0]


def test_record_identity(fly):
    assert fly.schema == "tspi-dcv/1"
    assert fly.launch.munition_id == "dart-01"
    assert fly.launch.launcher_id == "blue-01"
    assert fly.launch.target_id == "red-01"
    assert fly.outcome.terminal == "expire"


def test_frame_definition(f, fly):
    R = fly.frame.dcv_from_ned
    # Orthogonal (inverse == transpose) but an improper rotation: (D, C, V) with
    # crossrange right / vertical up is a left-handed triple by construction.
    np.testing.assert_allclose(R @ R.T, np.eye(3), atol=1e-12)
    assert np.linalg.det(R) == pytest.approx(-1.0)
    # Origin is the dart's launch position; V̂ is straight up.
    np.testing.assert_array_equal(fly.frame.origin_ned_m, f.samples("dart-01")[0]["pos"])
    np.testing.assert_array_equal(R[2], [0.0, 0.0, -1.0])
    # D̂ is horizontal and carries the full horizontal launch->target separation.
    assert fly.frame.downrange_ref == "target"
    idx = round((fly.launch.t_s - f.entity("red-01").t0_s) / f.dt_s)
    sep = f.samples("red-01")[idx]["pos"] - fly.frame.origin_ned_m
    assert R[0][2] == 0.0
    assert R[0] @ sep == pytest.approx(math.hypot(sep[0], sep[1]))


def test_munition_track_is_the_flyout(f, fly):
    # Starts at the frame origin at launch, ends at the terminal event.
    np.testing.assert_allclose(fly.munition.pos_dcv_m[0], 0.0, atol=1e-9)
    assert fly.munition.t_s[0] == pytest.approx(0.5)
    expire_t = next(ev.t_s for ev in f.events if ev.kind == "expire")
    assert fly.munition.t_s[-1] == pytest.approx(expire_t, abs=f.dt_s)
    assert fly.munition.t0_s == pytest.approx(0.5)


def test_target_starts_on_the_shot_line(f, fly):
    # By construction the target sits at (horizontal range, 0, rel. height) at launch.
    d0, c0, v0 = fly.target.pos_dcv_m[0]
    idx = round((fly.launch.t_s - f.entity("red-01").t0_s) / f.dt_s)
    tgt_at = f.samples("red-01")[idx]["pos"]
    assert d0 == pytest.approx(
        math.hypot(*(tgt_at - fly.frame.origin_ned_m)[:2]))
    assert c0 == pytest.approx(0.0, abs=1e-9)
    assert v0 == pytest.approx(-(tgt_at[2] - fly.frame.origin_ned_m[2]))


def test_ballistic_drop_reads_as_negative_vertical(fly):
    # Unguided dart in vacuum: height below launch = 0.5 g tau^2 exactly.
    tau = fly.munition.t_s - fly.munition.t_s[0]
    g = 9.80665
    np.testing.assert_allclose(fly.munition.pos_dcv_m[:, 2], -0.5 * g * tau**2, atol=1e-3)


def test_round_trip_to_ned(f, fly):
    R, origin = fly.frame.dcv_from_ned, fly.frame.origin_ned_m
    dart = f.samples("dart-01")[: len(fly.munition.t_s)]
    np.testing.assert_allclose(origin + fly.munition.pos_dcv_m @ R, dart["pos"], atol=1e-9)
    # Velocity conversion is an isometry: speeds are preserved.
    np.testing.assert_allclose(
        np.linalg.norm(fly.munition.vel_dcv_mps, axis=1),
        np.linalg.norm(dart["vel"].astype(np.float64), axis=1), atol=1e-6)


def test_dcv_basis_fallback_chain():
    from tspi_py.dcv import _dcv_basis

    lp = np.array([0.0, 0.0, -10.0])
    overhead = lp + np.array([0.0, 0.0, -5000.0])
    vertical = np.array([0.0, 0.0, -80.0])
    # Target overhead + vertical launch (ship VLS): the target's horizontal
    # velocity direction fixes the bearing.
    rot, ref = _dcv_basis(lp, overhead, vertical, np.array([0.0, -200.0, 0.0]))
    assert ref == "target_velocity"
    np.testing.assert_allclose(rot[0], [0.0, -1.0, 0.0], atol=1e-12)
    # Normal geometry is untouched.
    _, ref2 = _dcv_basis(lp, lp + np.array([1000.0, 0.0, -2000.0]),
                         vertical, np.array([-200.0, 0.0, 0.0]))
    assert ref2 == "target"
    # All three references vertical: no bearing exists.
    with pytest.raises(ValueError, match="degenerate"):
        _dcv_basis(lp, overhead, vertical, np.array([0.0, 0.0, -1.0]))


def test_save_mat_round_trips(f, fly, tmp_path):
    scipy_io = pytest.importorskip("scipy.io")
    from tspi_py.dcv import save_mat

    out = str(tmp_path / "flyouts.mat")
    save_mat(out, [fly])
    loaded = scipy_io.loadmat(out, simplify_cells=True)
    assert loaded["schema"] == "tspi-dcv/1"
    got = loaded["flyouts"]  # 1-element struct array simplifies to a dict
    np.testing.assert_allclose(got["munition"]["pos_dcv_m"], fly.munition.pos_dcv_m)
    np.testing.assert_allclose(got["frame"]["dcv_from_ned"], fly.frame.dcv_from_ned)
    assert got["launch"]["munition_id"] == "dart-01"


def test_windowing_matches_engagement_semantics(f):
    tight = dcv_flyouts(GOLDEN, window_s=1.0)[0]
    assert tight.munition.t_s[-1] == pytest.approx(1.5, abs=f.dt_s)
    assert tight.target.t_s[-1] == pytest.approx(1.5, abs=f.dt_s)
    full = dcv_flyouts(GOLDEN, window_s=None)[0]
    assert len(full.munition.t_s) == f.entity("dart-01").samples
    assert len(full.target.t_s) == f.entity("red-01").samples
