"""Pure-logic coverage for the E12 mixed-load benchmark KPI module (#401). No docker/gRPC/k6/Prometheus
access here — mixed_load_kpi.py only classifies/aggregates samples a harness
(s21_mixed_load_benchmark.py) collects elsewhere, mirroring the E11 split (s20_retention_compaction.py
vs lake_retention_kpi.py)."""
from __future__ import annotations

import importlib.util
import sys
from pathlib import Path

MODULE_PATH = Path(__file__).parents[1] / "mixed_load_kpi.py"
SPEC = importlib.util.spec_from_file_location("mixed_load_kpi", MODULE_PATH)
mixed_load_kpi = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = mixed_load_kpi
SPEC.loader.exec_module(mixed_load_kpi)


# ── percentile / latency_percentiles ────────────────────────────────────────────────────────────────

def test_latency_percentiles_computes_p50_p95_p99_max_min_count():
    values = [float(v) for v in range(1, 101)]  # 1..100

    summary = mixed_load_kpi.latency_percentiles(values)

    assert summary["count"] == 100
    assert summary["min_ms"] == 1.0
    assert summary["max_ms"] == 100.0
    # nearest-rank (ceil), same convention as lake_retention_kpi.percentile_summary
    assert summary["p50_ms"] == 50.0
    assert summary["p95_ms"] == 95.0
    assert summary["p99_ms"] == 99.0


def test_latency_percentiles_empty_is_all_zero_with_zero_count():
    summary = mixed_load_kpi.latency_percentiles([])

    assert summary == {"p50_ms": 0.0, "p95_ms": 0.0, "p99_ms": 0.0, "max_ms": 0.0, "min_ms": 0.0, "count": 0}


# ── classify_compaction_window / bucket_by_compaction_window ───────────────────────────────────────

def test_classify_compaction_window_before_flip_is_during():
    assert mixed_load_kpi.classify_compaction_window(10.0, converged_at_s=60.0) == "during"
    assert mixed_load_kpi.classify_compaction_window(60.0, converged_at_s=60.0) == "during"  # boundary itself


def test_classify_compaction_window_well_after_flip_is_not_during():
    assert mixed_load_kpi.classify_compaction_window(
        200.0, converged_at_s=60.0, post_settle_margin_s=60.0) == "not_during"


def test_classify_compaction_window_transition_zone_is_excluded():
    # Just after the flip, but inside the settle margin — not yet "well after".
    assert mixed_load_kpi.classify_compaction_window(
        90.0, converged_at_s=60.0, post_settle_margin_s=60.0) == "excluded"


def test_classify_compaction_window_never_converged_treats_everything_as_during():
    # Compaction never confirmed finished during the observation window — every sample is "in flight
    # or unconfirmed", not silently miscounted as the clean "not_during" baseline.
    assert mixed_load_kpi.classify_compaction_window(500.0, converged_at_s=None) == "during"


def test_bucket_by_compaction_window_splits_and_drops_the_transition_zone():
    samples = [
        {"elapsed_s": 10.0, "latency_ms": 5.0},   # during
        {"elapsed_s": 90.0, "latency_ms": 999.0},  # excluded (transition)
        {"elapsed_s": 200.0, "latency_ms": 3.0},   # not_during
        {"elapsed_s": 205.0, "latency_ms": 4.0},   # not_during
    ]

    buckets = mixed_load_kpi.bucket_by_compaction_window(samples, converged_at_s=60.0, post_settle_margin_s=60.0)

    assert buckets["during"] == [5.0]
    assert buckets["not_during"] == [3.0, 4.0]
    assert 999.0 not in buckets["during"] and 999.0 not in buckets["not_during"]


# ── ingress_compaction_comparison ───────────────────────────────────────────────────────────────────

def test_ingress_compaction_comparison_reports_higher_p95_during_compaction():
    samples = (
        [{"elapsed_s": t, "latency_ms": 50.0} for t in range(0, 60, 5)]      # during: slow
        + [{"elapsed_s": t, "latency_ms": 5.0} for t in range(200, 260, 5)]  # not_during: fast
    )

    result = mixed_load_kpi.ingress_compaction_comparison(samples, converged_at_s=60.0, post_settle_margin_s=60.0)

    assert result["ingest_e2e_during_compaction_samples"] == 12
    assert result["ingest_e2e_not_during_compaction_samples"] == 12
    assert result["ingest_e2e_during_compaction_p95_ms"] == 50.0
    assert result["ingest_e2e_not_during_compaction_p95_ms"] == 5.0
    assert result["ingest_e2e_compaction_p95_degradation_ratio"] == 10.0


def test_ingress_compaction_comparison_missing_one_bucket_reports_none_ratio():
    samples = [{"elapsed_s": t, "latency_ms": 50.0} for t in range(0, 60, 5)]  # during only

    result = mixed_load_kpi.ingress_compaction_comparison(samples, converged_at_s=60.0, post_settle_margin_s=60.0)

    assert result["ingest_e2e_not_during_compaction_samples"] == 0
    assert result["ingest_e2e_compaction_p95_degradation_ratio"] is None
    assert result["ingest_e2e_compaction_p99_degradation_ratio"] is None


# ── control_rtt_load_comparison ─────────────────────────────────────────────────────────────────────

def test_control_rtt_load_comparison_reports_degradation_ratio():
    baseline = [20.0] * 20
    concurrent = [60.0] * 20

    result = mixed_load_kpi.control_rtt_load_comparison(baseline, concurrent)

    assert result["control_rtt_baseline_p95_ms"] == 20.0
    assert result["control_rtt_concurrent_p95_ms"] == 60.0
    assert result["control_rtt_p95_degradation_ratio"] == 3.0


def test_control_rtt_load_comparison_empty_either_side_reports_none_ratio():
    assert mixed_load_kpi.control_rtt_load_comparison([], [60.0]) is not None
    result = mixed_load_kpi.control_rtt_load_comparison([], [60.0])
    assert result["control_rtt_p95_degradation_ratio"] is None
    result2 = mixed_load_kpi.control_rtt_load_comparison([20.0], [])
    assert result2["control_rtt_p95_degradation_ratio"] is None


# ── split_decision ───────────────────────────────────────────────────────────────────────────────────

def test_split_decision_marks_the_two_non_measurable_conditions_explicitly():
    decision = mixed_load_kpi.split_decision({})

    cond5 = decision["conditions"]["5_gateway_count_growth_needs_ingest_only_scaling"]
    cond6 = decision["conditions"]["6_kubernetes_slo_definition_stage"]
    assert cond5["status"] == "not_applicable_from_this_run"
    assert cond5["met"] is None
    assert cond6["status"] == "not_applicable_from_this_run"
    assert cond6["met"] is None


def test_split_decision_missing_metrics_reports_insufficient_data_not_false():
    # Absent data must not silently read as "condition not met" (that would be indistinguishable from
    # a real measurement showing no problem).
    decision = mixed_load_kpi.split_decision({})

    for key in (
        "1_ingest_cpu_sustained_high",
        "2_ingress_degrades_during_compaction",
        "3_writer_lag_increasing_trend",
        "4_control_rtt_degraded_by_telemetry_load",
    ):
        cond = decision["conditions"][key]
        assert cond["status"] == "insufficient_data"
        assert cond["met"] is None

    assert decision["measurable_conditions_evaluated"] == 0
    assert decision["split_recommended"] is None


def test_split_decision_all_four_measurable_conditions_met_recommends_split():
    metrics = {
        "ingest_cpu_high_ratio": 0.9,
        "ingest_e2e_compaction_p95_degradation_ratio": 3.0,
        "consumer_pending_slope_per_sec": 5.0,
        "control_rtt_p95_degradation_ratio": 2.0,
    }

    decision = mixed_load_kpi.split_decision(metrics)

    assert decision["measurable_conditions_evaluated"] == 4
    assert decision["measurable_conditions_met"] == 4
    assert decision["split_recommended"] is True
    for key in (
        "1_ingest_cpu_sustained_high",
        "2_ingress_degrades_during_compaction",
        "3_writer_lag_increasing_trend",
        "4_control_rtt_degraded_by_telemetry_load",
    ):
        assert decision["conditions"][key]["status"] == "measured"
        assert decision["conditions"][key]["met"] is True


def test_split_decision_all_measurable_conditions_clean_recommends_no_split():
    metrics = {
        "ingest_cpu_high_ratio": 0.1,
        "ingest_e2e_compaction_p95_degradation_ratio": 1.02,
        "consumer_pending_slope_per_sec": 0.0,
        "control_rtt_p95_degradation_ratio": 1.01,
    }

    decision = mixed_load_kpi.split_decision(metrics)

    assert decision["measurable_conditions_met"] == 0
    assert decision["split_recommended"] is False


def test_split_decision_respects_custom_thresholds():
    metrics = {"ingest_e2e_compaction_p95_degradation_ratio": 1.2}

    lenient = mixed_load_kpi.split_decision(metrics, ingress_p95_degradation_ratio_threshold=1.5)
    strict = mixed_load_kpi.split_decision(metrics, ingress_p95_degradation_ratio_threshold=1.1)

    assert lenient["conditions"]["2_ingress_degrades_during_compaction"]["met"] is False
    assert strict["conditions"]["2_ingress_degrades_during_compaction"]["met"] is True
