"""Cross-language contract test: reads the committed golden .tspi produced by the C# sim.

If these fail after a format change, either fix the regression or intentionally
bump the format version and regenerate the golden (see docs/FORMAT.md)."""

import math
import os

import numpy as np
import pytest

from tspi_py import TspiFile

GOLDEN = os.path.join(os.path.dirname(__file__), "data", "golden-v1.tspi")


@pytest.fixture(scope="module")
def f() -> TspiFile:
    return TspiFile(GOLDEN)


def test_header(f):
    assert f.version == 1
    assert f.dt_ns == 100_000_000  # dt = 0.1 s
    assert abs(f.origin_lat_deg - 34.9061) < 1e-9
    assert abs(f.origin_lon_deg - -117.8839) < 1e-9
    assert abs(f.origin_alt_m - 700.0) < 1e-9
    # epoch 2026-01-02T03:04:05Z
    assert f.epoch_unix_ns == 1_767_323_045_000_000_000


def test_entity_table(f):
    assert set(f.entities) == {"blue-01", "red-01", "dart-01"}
    blue = f.entity("blue-01")
    assert blue.team == "blue" and blue.type == "aircraft" and blue.parent is None
    assert blue.samples == 31  # 3 s at 10 Hz inclusive
    dart = f.entity("dart-01")
    assert dart.type == "munition"
    assert dart.parent == blue.ord
    assert dart.t0_ns == 500_000_000  # launched at t=0.5


def test_samples_match_manifest_initial_state(f):
    blue = f.samples("blue-01")
    assert blue.shape == (31,)
    np.testing.assert_allclose(blue["pos"][0], [0.0, 0.0, -5000.0], atol=1e-9)
    np.testing.assert_allclose(blue["vel"][0], [200.0, 0.0, 0.0], atol=1e-6)
    red = f.samples("red-01")
    np.testing.assert_allclose(red["pos"][0], [8000.0, 500.0, -5000.0], atol=1e-9)


def test_straight_flight_kinematics(f):
    # blue-01 flies straight and level in vacuum: pos.N == 200 * t exactly (fp tolerance).
    blue = f.samples("blue-01")
    t = f.times("blue-01")
    np.testing.assert_allclose(blue["pos"][:, 0], 200.0 * t, atol=1e-6)
    np.testing.assert_allclose(blue["pos"][:, 2], -5000.0, atol=1e-6)


def test_ballistic_dart_drops_under_gravity(f):
    # dart-01 is unguided in vacuum: d(t) = d0 + 0.5 g tau^2 (inherits level parent velocity).
    dart = f.samples("dart-01")
    t = f.times("dart-01")
    tau = t - t[0]
    g = 9.80665
    np.testing.assert_allclose(dart["pos"][:, 2], dart["pos"][0, 2] + 0.5 * g * tau**2, atol=1e-3)


def test_quaternions_are_unit_and_sign_continuous(f):
    for eid in f.entities:
        q = f.samples(eid)["quat"].astype(np.float64)
        norms = np.linalg.norm(q, axis=1)
        np.testing.assert_allclose(norms, 1.0, atol=1e-5)
        dots = np.sum(q[:-1] * q[1:], axis=1)
        assert (dots >= 0).all(), f"{eid}: quaternion sign flip breaks slerp continuity"


def test_events(f):
    kinds = [e.kind for e in f.events]
    assert kinds == ["launch", "expire"]
    launch = f.events[0]
    assert launch.t_s == pytest.approx(0.5)
    assert launch.src == f.entity("dart-01").ord
    assert launch.dst == f.entity("red-01").ord


def test_provenance_chain(f):
    assert len(f.provenance) == 1
    rec = f.provenance[0]
    assert rec["op"] == "run"
    assert rec["seed"] == 12345
    assert len(rec["manifest_sha256"]) == 64
    assert "generic-fighter" in rec["models"]


def test_times_are_implicit_fixed_dt(f):
    t = f.times("blue-01")
    np.testing.assert_allclose(np.diff(t), 0.1, atol=1e-12)
