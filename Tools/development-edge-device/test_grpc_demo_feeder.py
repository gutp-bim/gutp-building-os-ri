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
