"""TDD for resilience_metrics (E8, #246) — pure RTO / data-loss math, no I/O."""
import os
import sys

import pytest

sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
from resilience_metrics import build_e8_metrics, data_loss_ratio, rto_seconds  # noqa: E402


# ── rto_seconds ──────────────────────────────────────────────────────────────
def test_rto_seconds_basic():
    assert rto_seconds(100.0, 112.5) == pytest.approx(12.5)


def test_rto_seconds_clamps_negative_to_zero():
    # healthy observed before down (clock jitter / already-healthy) → 0, never negative
    assert rto_seconds(100.0, 99.0) == 0.0


# ── data_loss_ratio ──────────────────────────────────────────────────────────
def test_data_loss_zero_when_all_persisted():
    assert data_loss_ratio(1000, 1000) == 0.0


def test_data_loss_ratio_partial():
    assert data_loss_ratio(1000, 950) == pytest.approx(0.05)


def test_data_loss_zero_sent_is_zero():
    assert data_loss_ratio(0, 0) == 0.0


def test_data_loss_clamps_negative():
    # persisted > sent (e.g. redelivery dup counted) must not yield negative loss
    assert data_loss_ratio(1000, 1010) == 0.0


# ── build_e8_metrics ─────────────────────────────────────────────────────────
def test_build_e8_metrics_shape_and_values():
    m = build_e8_metrics(down_at=10.0, healthy_at=25.0, phase2_sent=500, phase2_persisted=500)
    assert m["data_loss_under_outage"] == 0.0
    assert m["rto_seconds"] == pytest.approx(15.0)


def test_build_e8_metrics_post_recovery_loss():
    m = build_e8_metrics(down_at=0.0, healthy_at=8.0, phase2_sent=200, phase2_persisted=198)
    assert m["data_loss_under_outage"] == pytest.approx(0.01)
    assert m["rto_seconds"] == pytest.approx(8.0)
