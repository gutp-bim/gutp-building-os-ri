"""Unit tests for the demo telemetry feeder's pure value/frame logic (#155).

These import only the pure helpers, which must be import-safe WITHOUT grpcio
installed (grpc/grpc_tools are lazy-imported inside the send path). Run with:
    python3 -m pytest Tools/development-edge-device/test_grpc_demo_feeder.py
"""

import os
import sys

sys.path.insert(0, os.path.dirname(__file__))

from grpc_demo_feeder import (  # noqa: E402
    DEFAULT_GATEWAY_ID,
    DEMO_POINTS,
    PointSpec,
    build_frame_values,
    demo_value,
)


def test_eight_points_match_seed_twin():
    # Must mirror fixtures/e2e/twin.ttl (GW-SOS-001 / SOS-PT-001..008).
    ids = [p.point_id for p in DEMO_POINTS]
    assert ids == [f"SOS-PT-00{i}" for i in range(1, 9)]
    assert DEFAULT_GATEWAY_ID == "GW-SOS-001"


def test_number_value_stays_in_amplitude_band():
    spec = PointSpec("X", "number", 24.0, 2.0, 300.0)
    for t in range(0, 600, 7):
        v = demo_value(spec, float(t))
        assert 22.0 - 1e-9 <= v <= 26.0 + 1e-9


def test_bounded_number_is_clamped():
    # Amplitude deliberately larger than the [lo, hi] band → must clamp.
    spec = PointSpec("SP", "number", 23.0, 50.0, 300.0, lo=16.0, hi=30.0)
    for t in range(0, 600, 5):
        v = demo_value(spec, float(t))
        assert 16.0 <= v <= 30.0


def test_boolean_is_zero_or_one_and_toggles():
    spec = PointSpec("B", "boolean", 0.0, 0.0, 240.0)
    vals = {demo_value(spec, float(t)) for t in range(0, 480, 3)}
    assert vals <= {0.0, 1.0}
    assert vals == {0.0, 1.0}  # both states appear across a full period


def test_enum_spans_full_range():
    spec = PointSpec("F", "enum", 0.0, 0.0, 480.0, lo=0.0, hi=3.0)
    vals = {demo_value(spec, float(t)) for t in range(0, 960, 5)}
    assert vals <= {0.0, 1.0, 2.0, 3.0}
    assert 0.0 in vals and 3.0 in vals


def test_build_frame_values_shape_and_order():
    frames = build_frame_values(DEMO_POINTS, 12.0)
    assert len(frames) == 8
    assert all(isinstance(pid, str) and isinstance(v, float) for pid, v in frames)
    assert [pid for pid, _ in frames] == [p.point_id for p in DEMO_POINTS]


def test_deterministic_for_same_elapsed():
    assert build_frame_values(DEMO_POINTS, 33.0) == build_frame_values(DEMO_POINTS, 33.0)


# ── Plan-driven dataset (#458) ───────────────────────────────────────────────
import json  # noqa: E402
from pathlib import Path  # noqa: E402

from grpc_demo_feeder import (  # noqa: E402
    FeedPoint,
    connected_gateways,
    due_frames,
    initial_frames,
    legacy_feed_points,
    load_plan,
)

PLAN_PATH = Path(__file__).resolve().parents[2] / "fixtures" / "demo" / "feeder-plan.json"


def _fp(pid, role="normal", interval=60.0, gateway="GW-A", **kw):
    spec = PointSpec(pid, kw.pop("kind", "number"), kw.pop("base", 20.0), kw.pop("amplitude", 1.0),
                     kw.pop("period", 600.0))
    return FeedPoint(spec=spec, gateway_id=gateway, interval_s=interval, role=role, **kw)


def test_zero_period_is_a_constant_reading():
    spec = PointSpec("C", "boolean", 0.0, 0.0, 0.0)
    assert {demo_value(spec, float(t)) for t in range(0, 100, 7)} == {0.0}


def test_phase_offset_desynchronises_points_of_one_template():
    a = PointSpec("A", "number", 24.0, 2.0, 600.0, phase_s=0.0)
    b = PointSpec("B", "number", 24.0, 2.0, 600.0, phase_s=150.0)
    assert demo_value(a, 0.0) != demo_value(b, 0.0)


def test_load_plan_reads_the_committed_plan():
    points, gateways, down = load_plan(str(PLAN_PATH), scenario="degraded")
    assert len(points) > 1000
    assert down == {"GW-DEMO-RF"}
    assert "GW-DEMO-1F" in gateways
    roles = {p.role for p in points}
    assert roles == {"normal", "stale", "missing", "alarm"}
    # per-point phase so 64 room temps do not move in lockstep
    temps = [p for p in points if p.spec.point_id.endswith("-T")]
    assert len({p.spec.phase_s for p in temps}) > 10


def test_load_plan_healthy_scenario_makes_everything_normal():
    points, gateways, down = load_plan(str(PLAN_PATH), scenario="healthy")
    assert {p.role for p in points} == {"normal"}
    assert down == set()


def test_down_gateway_points_are_never_sent():
    points, _, down = load_plan(str(PLAN_PATH), scenario="degraded")
    frames = initial_frames(points, down, now_epoch=1_000_000.0)
    assert not any(gw in down for gw, *_ in frames)


def test_unknown_scenario_fails_fast():
    import pytest

    with pytest.raises(ValueError):
        load_plan(str(PLAN_PATH), scenario="chaos")


def test_initial_frames_backdate_stale_and_skip_missing():
    pts = [_fp("N"), _fp("S", role="stale", interval=60.0), _fp("M", role="missing"),
           _fp("A", role="alarm", alarm_value=33.5)]
    frames = {pid: (gw, v, ts) for gw, pid, v, ts in initial_frames(pts, set(), now_epoch=10_000.0)}
    assert set(frames) == {"N", "S", "A"}
    assert frames["N"][2] == 10_000.0
    # stale: one reading at now − interval × 4, i.e. past the interval × 3 threshold from second one
    assert frames["S"][2] == 10_000.0 - 240.0
    assert frames["A"][1] == 33.5


def test_due_frames_respect_each_points_interval_and_never_resend_stale():
    pts = [_fp("FAST", interval=5.0), _fp("SLOW", interval=60.0), _fp("S", role="stale", interval=5.0),
           _fp("M", role="missing", interval=5.0)]
    last = {"FAST": 100.0, "SLOW": 100.0, "S": 100.0}
    due = due_frames(pts, set(), last, now_epoch=106.0)
    assert [pid for _, pid, _, _ in due] == ["FAST"]
    due = due_frames(pts, set(), last, now_epoch=161.0)
    assert sorted(pid for _, pid, _, _ in due) == ["FAST", "SLOW"]


def test_legacy_points_keep_streaming_alongside_the_plan():
    legacy = legacy_feed_points("GW-SOS-001", 5.0)
    assert [p.spec.point_id for p in legacy] == [p.point_id for p in DEMO_POINTS]
    assert all(p.role == "normal" and p.interval_s == 5.0 for p in legacy)


def test_connected_gateways_excludes_the_down_ones():
    assert connected_gateways({"GW-A", "GW-B", "GW-SOS-001"}, {"GW-B"}) == ["GW-A", "GW-SOS-001"]


def test_plan_file_is_valid_json():
    json.loads(PLAN_PATH.read_text(encoding="utf-8"))
