"""Pure-logic coverage for the E12 mixed-load benchmark harness (#401): CLI arg parsing/defaults,
docker-compose service selection for all-in-one vs role-split mode, docker-stats CPU% parsing and
aggregation, k6 raw-metric extraction, and result-dict shaping — everything s21 can compute with no
live docker/gRPC/k6/Prometheus access. Mirrors the precedent set by tests/test_s20_retention_compaction.py.
"""
from __future__ import annotations

import importlib.util
import sys
from pathlib import Path

MODULE_PATH = Path(__file__).parents[1] / "s21_mixed_load_benchmark.py"
SPEC = importlib.util.spec_from_file_location("s21_mixed_load_benchmark", MODULE_PATH)
s21 = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = s21
SPEC.loader.exec_module(s21)


# ── CLI arg parsing ──────────────────────────────────────────────────────────────────────────────────

def test_parse_args_defaults():
    args = s21.parse_args([])

    assert args.role_mode == "all"
    assert args.duration_s > 0
    assert args.ingest_rate > 0
    assert args.ingest_points > 0
    assert args.control_vus > 0
    assert args.target_hours_back >= 1
    assert args.out == "results/E12"


def test_parse_args_role_mode_split_is_accepted():
    args = s21.parse_args(["--role-mode", "split"])
    assert args.role_mode == "split"


def test_parse_args_rejects_unknown_role_mode():
    import pytest

    with pytest.raises(SystemExit):
        s21.parse_args(["--role-mode", "bogus"])


def test_parse_args_overrides_are_applied():
    args = s21.parse_args(["--duration-s", "120", "--ingest-rate", "50", "--out", "/tmp/e12"])
    assert args.duration_s == 120
    assert args.ingest_rate == 50
    assert args.out == "/tmp/e12"


# ── docker-compose service selection ────────────────────────────────────────────────────────────────

def test_docker_compose_up_args_all_mode_targets_the_single_connector_worker():
    argv = s21.docker_compose_up_args("all", "docker-compose.oss.yaml", "docker-compose.roles.yaml")

    assert argv[:2] == ["docker", "compose"]
    assert "docker-compose.roles.yaml" not in argv
    assert "building-os.connector-worker" in argv
    assert "building-os.connector-worker-lake" not in argv
    assert "building-os.connector-worker-control" not in argv


def test_docker_compose_up_args_split_mode_adds_the_roles_overlay_and_the_two_extra_services():
    argv = s21.docker_compose_up_args("split", "docker-compose.oss.yaml", "docker-compose.roles.yaml")

    assert "docker-compose.oss.yaml" in argv
    assert "docker-compose.roles.yaml" in argv
    # roles overlay file must come after the base file as a second -f (compose merge order)
    assert argv.index("docker-compose.roles.yaml") > argv.index("docker-compose.oss.yaml")
    assert "building-os.connector-worker" in argv
    assert "building-os.connector-worker-lake" in argv
    assert "building-os.connector-worker-control" in argv


def test_docker_compose_up_args_rejects_unknown_mode():
    import pytest

    with pytest.raises(ValueError):
        s21.docker_compose_up_args("bogus", "a.yaml", "b.yaml")


# ── docker stats CPU% parsing/aggregation ───────────────────────────────────────────────────────────

def test_parse_cpu_percent_parses_a_docker_stats_field():
    assert s21.parse_cpu_percent("12.34%") == 12.34
    assert s21.parse_cpu_percent("0.00%") == 0.0


def test_parse_cpu_percent_unparsable_is_none():
    assert s21.parse_cpu_percent("") is None
    assert s21.parse_cpu_percent("n/a") is None


def test_cpu_high_ratio_fraction_of_samples_at_or_above_threshold():
    samples = [70.0, 80.0, 10.0, 90.0]  # 3/4 >= 65
    assert s21.cpu_high_ratio(samples, threshold_pct=65.0) == 0.75


def test_cpu_high_ratio_no_samples_is_none_not_zero():
    # Absence of data must not read as "measured 0% high-CPU" — split_decision relies on this to
    # report insufficient_data rather than a false "condition not met".
    assert s21.cpu_high_ratio([], threshold_pct=65.0) is None


# ── k6 raw-metric extraction (--out json=<file> line format) ────────────────────────────────────────

def test_parse_k6_json_metric_lines_extracts_matching_point_values():
    lines = [
        '{"type":"Point","metric":"control_submission_duration","data":{"value":22.5}}',
        '{"type":"Point","metric":"http_req_duration","data":{"value":999.0}}',
        '{"type":"Metric","metric":"control_submission_duration"}',
        'not even json',
        '{"type":"Point","metric":"control_submission_duration","data":{"value":30.1}}',
    ]

    values = s21.parse_k6_json_metric_lines(lines, "control_submission_duration")

    assert values == [22.5, 30.1]


def test_parse_k6_json_metric_lines_empty_input_is_empty_list():
    assert s21.parse_k6_json_metric_lines([], "control_submission_duration") == []


# ── result-dict shaping ──────────────────────────────────────────────────────────────────────────────

def test_build_kpi_summary_shapes_the_canonical_axis_metrics_envelope():
    config = {"role_mode": "all", "duration_s": 60}
    ingress_metrics = {"ingest_e2e_during_compaction_p95_ms": 10.0, "ingest_e2e_not_during_compaction_p95_ms": 5.0}
    control_metrics = {"control_rtt_baseline_p95_ms": 20.0, "control_rtt_concurrent_p95_ms": 25.0}
    resource_metrics = {"ingest_cpu_high_ratio": 0.5, "consumer_pending_slope_per_sec": 0.1}
    decision = {"split_recommended": False, "measurable_conditions_met": 0,
                "measurable_conditions_evaluated": 4, "conditions": {}}

    result = s21.build_kpi_summary(config, ingress_metrics, control_metrics, resource_metrics, decision)

    assert result["axis"] == "E12_mixed_load_benchmark"
    assert result["config"] == config
    assert "generated_at" in result
    assert result["metrics"]["ingest_e2e_during_compaction_p95_ms"] == 10.0
    assert result["metrics"]["control_rtt_baseline_p95_ms"] == 20.0
    assert result["metrics"]["ingest_cpu_high_ratio"] == 0.5
    assert result["split_decision"] == decision
    # gate.py (e2e/runner/gate.py) only ever reads a result JSON's `metrics` dict — kpi-thresholds.yaml
    # gates the split-decision summary too, so it must also be flattened in there, not left reachable
    # only via the richer `split_decision` object.
    assert result["metrics"]["split_decision_split_recommended"] is False
    assert result["metrics"]["split_decision_measurable_conditions_met"] == 0
    assert result["metrics"]["split_decision_measurable_conditions_evaluated"] == 4


def test_build_kpi_summary_metric_dicts_do_not_collide():
    # A regression guard: the metric sources must merge into one namespace without one silently
    # overwriting another (they use disjoint key prefixes by construction).
    config = {}
    decision = {"split_recommended": None, "measurable_conditions_met": 0,
                "measurable_conditions_evaluated": 0, "conditions": {}}
    result = s21.build_kpi_summary(config, {"a": 1}, {"b": 2}, {"c": 3}, decision)
    assert result["metrics"] == {
        "a": 1, "b": 2, "c": 3,
        "split_decision_split_recommended": None,
        "split_decision_measurable_conditions_met": 0,
        "split_decision_measurable_conditions_evaluated": 0,
    }


# ── Markdown report rendering ────────────────────────────────────────────────────────────────────────

def test_render_report_md_includes_the_verdict_and_key_metrics():
    result = {
        "axis": "E12_mixed_load_benchmark",
        "generated_at": "2026-09-08T00:00:00+00:00",
        "config": {"role_mode": "all", "duration_s": 300},
        "metrics": {
            "ingest_e2e_during_compaction_p95_ms": 12.3,
            "ingest_e2e_not_during_compaction_p95_ms": 4.1,
            "control_rtt_baseline_p95_ms": 20.0,
            "control_rtt_concurrent_p95_ms": 25.0,
        },
        "split_decision": {
            "split_recommended": False,
            "measurable_conditions_evaluated": 4,
            "measurable_conditions_met": 0,
            "rationale": "0/4 measurable #399 conditions met",
            "conditions": {
                "1_ingest_cpu_sustained_high": {"status": "measured", "met": False, "value": 0.1, "threshold": 0.8},
                "5_gateway_count_growth_needs_ingest_only_scaling": {
                    "status": "not_applicable_from_this_run", "met": None, "value": None, "threshold": None,
                    "note": "n/a"},
            },
        },
    }

    md = s21.render_report_md(result)

    assert "E12" in md
    assert "role_mode" in md or "all" in md
    assert "12.3" in md
    assert "not_applicable_from_this_run" in md
    assert "split_recommended" in md.lower() or "Split" in md
