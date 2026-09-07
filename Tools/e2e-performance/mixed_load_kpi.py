#!/usr/bin/env python3
"""Pure KPI logic for E12 — mixed-load benchmark: ingest/control tail latency during compaction,
role-split (WORKER_ROLE) vs all-in-one (#401, child ② of the #399 Capability-based Worker Runtime PRD).

No docker/gRPC/k6/Prometheus access here — this module only classifies and aggregates samples a
harness (``s21_mixed_load_benchmark.py``) collects elsewhere, the same split E11 established between
``s20_retention_compaction.py`` (I/O) and ``lake_retention_kpi.py`` (pure KPI math).

Three independent things this axis has to get right, split into three families of functions:

  1. **Ingest E2E latency, bucketed by whether it was sampled during a live compaction cycle.**
     ``classify_compaction_window`` / ``bucket_by_compaction_window`` / ``ingress_compaction_comparison``.
     The harness detects "compaction is active" the way ``s20_retention_compaction.py`` already does —
     polling MinIO for the part-*.parquet → compact-*.parquet transition (``compaction_converged``) —
     and passes this module the wall-clock elapsed second that transition first flipped true
     (``converged_at_s``). Samples timestamped at or before that flip are "during" (compaction was
     still preparing/running, or the run's compaction never confirmed finished and *every* sample is
     conservatively "during" rather than silently miscounted as the clean baseline); samples well
     after are "not_during"; a settle-margin transition zone right after the flip is dropped from both
     buckets rather than guessed into either one.
  2. **Control RTT with vs without concurrent telemetry load.** ``control_rtt_load_comparison`` takes
     two independently-collected sample lists (a k6 s6_point_control.js run with no ingest traffic in
     flight, and one run concurrently with sustained ingest) and reports the degradation ratio.
  3. **The #399 split-decision judgment.** ``split_decision`` evaluates the four conditions a single
     benchmark run can actually measure (ingest CPU sustained high, ingress p95/p99 degradation during
     compaction, JetStream writer-lag trend, control RTT dragged down by telemetry load) against
     caller-supplied thresholds, and explicitly marks the other two named conditions from #399
     (gateway-fleet growth needing ingest-only scaling; reaching a Kubernetes SLO-definition stage) as
     ``not_applicable_from_this_run`` — organizational/deployment-context judgments no single run's
     numbers can settle — rather than omitting them.
"""

from __future__ import annotations

import math

# How long after `compaction_converged` first flips true a sample must wait before it counts as
# genuinely "not_during" (rather than "still settling"). Mirrors the general shape of s20's own
# compaction_wait_seconds margin — compaction convergence detection has some polling latency, so the
# moment right after the flip is not yet a clean baseline.
DEFAULT_POST_SETTLE_MARGIN_S = 60.0


def percentile(sorted_values: list[float], q: float) -> float:
    """Nearest-rank percentile (ceil-based), same convention as lake_retention_kpi.percentile_summary
    (E11) so E12's latency KPIs read the same way. 0.0 for an empty input — callers check `count`
    before trusting a percentile."""
    if not sorted_values:
        return 0.0
    rank = max(0, min(len(sorted_values) - 1, math.ceil(q * len(sorted_values)) - 1))
    return sorted_values[rank]


def latency_percentiles(values: list[float]) -> dict:
    """p50/p95/p99/max/min/count over one bucket of latency samples (ms). `count` is the caller's
    signal for whether the other fields (all 0.0 on empty input) mean anything."""
    sv = sorted(values)
    return {
        "p50_ms": round(percentile(sv, 0.50), 3),
        "p95_ms": round(percentile(sv, 0.95), 3),
        "p99_ms": round(percentile(sv, 0.99), 3),
        "max_ms": round(sv[-1], 3) if sv else 0.0,
        "min_ms": round(sv[0], 3) if sv else 0.0,
        "count": len(sv),
    }


def _safe_ratio(numerator: float | None, denominator: float | None) -> float | None:
    if numerator is None or denominator in (None, 0):
        return None
    return round(numerator / denominator, 4)


def classify_compaction_window(elapsed_s: float, converged_at_s: float | None,
                                post_settle_margin_s: float = DEFAULT_POST_SETTLE_MARGIN_S) -> str:
    """Classify one latency sample's wall-clock position relative to when
    ``s20_retention_compaction.compaction_converged`` first flipped true, for bucketing ingest/control
    latency by compaction activity (#401's core measurement).

    - ``converged_at_s is None`` — the run's forced compaction cycle never confirmed converged inside
      the observation window (the harness's polling gave up). Every sample is classified "during":
      compaction was either still running or its state is unconfirmed, and reporting it as the clean
      "not_during" baseline would be a false negative for degradation, not a neutral default.
    - ``elapsed_s <= converged_at_s`` — "during" (compaction preparation/execution, including the
      settled-hour wave ingest that precedes the compactor's own scan-and-merge).
    - ``elapsed_s >= converged_at_s + post_settle_margin_s`` — "not_during" (well after convergence).
    - otherwise — "excluded": a transition zone right after the flip. Convergence detection is itself
      a poll (see s20's ``compaction_converged``), so the instant right after it first reports True is
      not yet a clean "compaction is over" baseline; guessing it into either bucket would blur the
      comparison this axis exists to make.
    """
    if converged_at_s is None:
        return "during"
    if elapsed_s <= converged_at_s:
        return "during"
    if elapsed_s >= converged_at_s + post_settle_margin_s:
        return "not_during"
    return "excluded"


def bucket_by_compaction_window(samples: list[dict], converged_at_s: float | None,
                                 post_settle_margin_s: float = DEFAULT_POST_SETTLE_MARGIN_S
                                 ) -> dict[str, list[float]]:
    """samples: [{"elapsed_s": float, "latency_ms": float}, ...]. Returns {"during": [...],
    "not_during": [...]} — samples classified "excluded" (the settle transition zone) are dropped
    from both, not silently folded into either one."""
    buckets: dict[str, list[float]] = {"during": [], "not_during": []}
    for s in samples:
        label = classify_compaction_window(s["elapsed_s"], converged_at_s, post_settle_margin_s)
        if label in buckets:
            buckets[label].append(s["latency_ms"])
    return buckets


def ingress_compaction_comparison(samples: list[dict], converged_at_s: float | None,
                                   post_settle_margin_s: float = DEFAULT_POST_SETTLE_MARGIN_S) -> dict:
    """Buckets `samples` into during/not_during compaction windows and reports per-bucket ingest E2E
    percentiles plus the degradation ratios #399's condition 2 needs (p95/p99 during ÷ not_during).
    A ratio is None (not 1.0 or 0.0) whenever either bucket is empty — no comparison can be made, and
    reporting a numeric ratio there would misrepresent an unmeasured condition as a measured one."""
    buckets = bucket_by_compaction_window(samples, converged_at_s, post_settle_margin_s)
    during = latency_percentiles(buckets["during"])
    not_during = latency_percentiles(buckets["not_during"])
    have_both = during["count"] > 0 and not_during["count"] > 0
    return {
        "ingest_e2e_during_compaction_p50_ms": during["p50_ms"],
        "ingest_e2e_during_compaction_p95_ms": during["p95_ms"],
        "ingest_e2e_during_compaction_p99_ms": during["p99_ms"],
        "ingest_e2e_during_compaction_samples": during["count"],
        "ingest_e2e_not_during_compaction_p50_ms": not_during["p50_ms"],
        "ingest_e2e_not_during_compaction_p95_ms": not_during["p95_ms"],
        "ingest_e2e_not_during_compaction_p99_ms": not_during["p99_ms"],
        "ingest_e2e_not_during_compaction_samples": not_during["count"],
        "ingest_e2e_compaction_p95_degradation_ratio": (
            _safe_ratio(during["p95_ms"], not_during["p95_ms"]) if have_both else None),
        "ingest_e2e_compaction_p99_degradation_ratio": (
            _safe_ratio(during["p99_ms"], not_during["p99_ms"]) if have_both else None),
    }


def control_rtt_load_comparison(baseline_values: list[float], concurrent_values: list[float]) -> dict:
    """baseline_values: control RTT samples (ms) from a k6 s6_point_control.js run with no concurrent
    ingest traffic. concurrent_values: the same, run concurrently with sustained telemetry ingest.
    Reports both distributions plus the degradation ratios #399's condition 4 needs (concurrent ÷
    baseline). A ratio is None whenever either side has no samples."""
    baseline = latency_percentiles(baseline_values)
    concurrent = latency_percentiles(concurrent_values)
    have_both = baseline["count"] > 0 and concurrent["count"] > 0
    return {
        "control_rtt_baseline_p50_ms": baseline["p50_ms"],
        "control_rtt_baseline_p95_ms": baseline["p95_ms"],
        "control_rtt_baseline_p99_ms": baseline["p99_ms"],
        "control_rtt_baseline_samples": baseline["count"],
        "control_rtt_concurrent_p50_ms": concurrent["p50_ms"],
        "control_rtt_concurrent_p95_ms": concurrent["p95_ms"],
        "control_rtt_concurrent_p99_ms": concurrent["p99_ms"],
        "control_rtt_concurrent_samples": concurrent["count"],
        "control_rtt_p95_degradation_ratio": (
            _safe_ratio(concurrent["p95_ms"], baseline["p95_ms"]) if have_both else None),
        "control_rtt_p99_degradation_ratio": (
            _safe_ratio(concurrent["p99_ms"], baseline["p99_ms"]) if have_both else None),
    }


NOT_APPLICABLE_STATUS = "not_applicable_from_this_run"


def _measured_condition(value, *, met_predicate, threshold) -> dict:
    """A condition backed by a metric this run may or may not have produced a value for.
    `status: "insufficient_data"` (not a false `met`) when the metric is absent — absence must not
    read as "measured and clean", which is indistinguishable from a real non-issue."""
    if value is None:
        return {"status": "insufficient_data", "met": None, "value": None, "threshold": threshold}
    return {"status": "measured", "met": bool(met_predicate(value)), "value": value, "threshold": threshold}


def _not_applicable_condition(note: str) -> dict:
    return {"status": NOT_APPLICABLE_STATUS, "met": None, "value": None, "threshold": None, "note": note}


def split_decision(metrics: dict, *,
                    ingest_cpu_high_ratio_threshold: float = 0.8,
                    ingress_p95_degradation_ratio_threshold: float = 1.5,
                    writer_lag_slope_threshold: float = 1.0,
                    control_rtt_degradation_ratio_threshold: float = 1.5) -> dict:
    """Evaluates #399's six named conditions for whether a ConnectorWorker deployment split
    (separate ingest/lake/control processes, `docker-compose.roles.yaml`) is necessary, against one
    mixed-load benchmark run's measurements (#401).

    Conditions 1-4 are the ones a single benchmark run can actually measure and are scored against
    `metrics` (expected keys: `ingest_cpu_high_ratio` — fraction of resource samples with ingest-role
    CPU at/above the ~60-70% band; `ingest_e2e_compaction_p95_degradation_ratio` — from
    `ingress_compaction_comparison`; `consumer_pending_slope_per_sec` — the same JetStream writer-lag
    slope `kpi_sampler.py`/E10 already compute; `control_rtt_p95_degradation_ratio` — from
    `control_rtt_load_comparison`). Each caller-supplied threshold is the *sole* gate for its
    condition — thresholds are exposed as parameters, not hardcoded, so the harness/report can vary
    them without touching this function.

    Conditions 5-6 (gateway-count growth needing only-ingest scaling; reaching a Kubernetes
    SLO-definition stage) are organizational/deployment-context judgments no single run's numbers can
    settle — they are always reported as `not_applicable_from_this_run`, never silently omitted, so a
    reader of the verdict sees exactly which of #399's six conditions this run could and could not
    speak to.
    """
    conditions = {
        "1_ingest_cpu_sustained_high": _measured_condition(
            metrics.get("ingest_cpu_high_ratio"),
            met_predicate=lambda v: v >= ingest_cpu_high_ratio_threshold,
            threshold=ingest_cpu_high_ratio_threshold),
        "2_ingress_degrades_during_compaction": _measured_condition(
            metrics.get("ingest_e2e_compaction_p95_degradation_ratio"),
            met_predicate=lambda v: v >= ingress_p95_degradation_ratio_threshold,
            threshold=ingress_p95_degradation_ratio_threshold),
        "3_writer_lag_increasing_trend": _measured_condition(
            metrics.get("consumer_pending_slope_per_sec"),
            met_predicate=lambda v: v > writer_lag_slope_threshold,
            threshold=writer_lag_slope_threshold),
        "4_control_rtt_degraded_by_telemetry_load": _measured_condition(
            metrics.get("control_rtt_p95_degradation_ratio"),
            met_predicate=lambda v: v >= control_rtt_degradation_ratio_threshold,
            threshold=control_rtt_degradation_ratio_threshold),
        "5_gateway_count_growth_needs_ingest_only_scaling": _not_applicable_condition(
            "requires observing gateway-fleet growth over time / fleet-wide capacity planning; a "
            "single benchmark run cannot measure this"),
        "6_kubernetes_slo_definition_stage": _not_applicable_condition(
            "organizational/deployment-context decision (has the project reached the stage of "
            "writing Kubernetes SLOs), not something a benchmark run can determine"),
    }

    measured = [c for c in conditions.values() if c["status"] == "measured"]
    met_count = sum(1 for c in measured if c["met"])
    return {
        "conditions": conditions,
        "measurable_conditions_evaluated": len(measured),
        "measurable_conditions_met": met_count,
        # None (not False) when this run produced no measurable data at all — "no evidence for a
        # split" and "we couldn't check" must not collapse into the same boolean.
        "split_recommended": (met_count > 0) if measured else None,
        "rationale": (
            f"{met_count}/{len(measured)} measurable #399 conditions met"
            if measured else "no measurable #399 condition had data this run"),
    }
