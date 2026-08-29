import importlib.util
from pathlib import Path
import sys


MODULE_PATH = Path(__file__).parents[1] / "s17_multibuilding_scale_sweep.py"
SPEC = importlib.util.spec_from_file_location("scale_sweep", MODULE_PATH)
scale_sweep = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = scale_sweep
SPEC.loader.exec_module(scale_sweep)


def load_stage_module():
    path = Path(__file__).parents[1] / "s17_scale_stage.py"
    spec = importlib.util.spec_from_file_location("scale_stage", path)
    module = importlib.util.module_from_spec(spec)
    sys.modules[spec.name] = module
    spec.loader.exec_module(module)
    return module


def test_topology_is_deterministic_and_balanced_across_buildings_and_gateways():
    first = scale_sweep.build_topology(2_000, buildings=4, gateways=8, run_id="run-1")
    second = scale_sweep.build_topology(2_000, buildings=4, gateways=8, run_id="run-1")

    assert first == second
    assert len(first) == 2_000
    assert len({point.building_id for point in first}) == 4
    assert len({point.gateway_id for point in first}) == 8
    assert max(scale_sweep.count_by(first, "building_id").values()) == 500
    assert max(scale_sweep.count_by(first, "gateway_id").values()) == 250


def test_evaluate_stage_reports_every_kpi_and_threshold_failure():
    result = scale_sweep.evaluate_stage(
        scale=5_000,
        point_list_ms=650.0,
        accepted=4_999,
        rejected=10,
        expected_accepted=5_000,
        expected_rejected=10,
        lake_rows=4_998,
        flush_ms=31_000.0,
        thresholds=scale_sweep.Thresholds(point_list_ms=500, loss_rate=0, flush_ms=30_000),
    )

    assert result["metrics"]["accepted"] == 4_999
    assert result["metrics"]["rejected"] == 10
    assert result["metrics"]["loss"] == 2
    assert result["passed"] is False
    assert set(result["exceeded_thresholds"]) == {"point_list_ms", "loss_rate", "flush_ms"}


def test_exit_code_identifies_first_failed_scale():
    results = [
        {"scale": 2_000, "passed": True},
        {"scale": 5_000, "passed": False},
        {"scale": 10_000, "passed": False},
    ]

    assert scale_sweep.failure_exit_code(results) == 2
    assert scale_sweep.failure_exit_code([{"scale": 2_000, "passed": True}]) == 0


def test_markdown_only_claims_largest_passing_scale():
    report = scale_sweep.render_markdown([
        scale_sweep.passing_result(2_000),
        scale_sweep.passing_result(5_000),
        {**scale_sweep.passing_result(10_000), "passed": False,
         "exceeded_thresholds": ["point_list_ms"]},
    ], run_id="run-1")

    assert "実測済み最大規模: **5,000 Point**" in report
    assert "10,000 | FAIL" in report


def test_stage_command_defaults_to_real_repository_runner():
    parser = scale_sweep.create_parser()
    args = parser.parse_args([])

    assert "s17_scale_stage.py" in args.stage_command
    assert "--topology {topology}" in args.stage_command
    assert "--output {output}" in args.stage_command


def test_stage_reports_pointlist_ingress_rejection_and_lake_flush():
    stage = load_stage_module()
    topology = [
        {"point_id": "p1", "building_id": "b1", "gateway_id": "g1"},
        {"point_id": "p2", "building_id": "b2", "gateway_id": "g2"},
    ]

    class Boundary:
        def seed(self, points): pass
        def refresh_services(self): pass
        def point_list_milliseconds(self, gateways): return [12.0, 18.0]
        def ingest(self, frames):
            return 0 if frames and frames[0][1].startswith("unknown-") else len(frames)
        def lake_rows(self, buildings): return 0 if self.polls == 0 else 2
        def cleanup(self): pass
        polls = 0
        def wait(self, seconds): self.polls += 1

    result = stage.measure(topology, Boundary(), invalid_per_gateway=1,
                           flush_timeout_s=5, poll_interval_s=0.01)

    assert result == {
        "point_list_ms": 18.0,
        "accepted": 2,
        "rejected": 2,
        "expected_accepted": 2,
        "expected_rejected": 2,
        "lake_rows": 2,
        "flush_ms": 10.0,
    }


def test_report_renders_stage_runner_failure_without_metrics():
    report = scale_sweep.render_markdown([
        {"scale": 2_000, "metrics": {}, "passed": False,
         "exceeded_thresholds": ["stage_runner"]},
    ], "failed-run")

    assert "2,000 | FAIL" in report
    assert "stage_runner" in report


def test_evaluate_stage_uses_per_scale_threshold_override_when_present():
    thresholds = scale_sweep.Thresholds(
        point_list_ms=5_000, point_list_ms_by_scale={100_000: 20_000})

    at_override_scale = scale_sweep.evaluate_stage(
        scale=100_000, point_list_ms=15_000, accepted=1, rejected=0,
        expected_accepted=1, expected_rejected=0, lake_rows=1, flush_ms=0,
        thresholds=thresholds)
    at_unlisted_scale = scale_sweep.evaluate_stage(
        scale=50_000, point_list_ms=6_000, accepted=1, rejected=0,
        expected_accepted=1, expected_rejected=0, lake_rows=1, flush_ms=0,
        thresholds=thresholds)

    assert at_override_scale["passed"] is True
    assert at_override_scale["metrics"]["point_list_ms_threshold"] == 20_000
    assert at_unlisted_scale["passed"] is False
    assert "point_list_ms" in at_unlisted_scale["exceeded_thresholds"]
    assert at_unlisted_scale["metrics"]["point_list_ms_threshold"] == 5_000


def test_parse_scale_ms_overrides_reads_pairs_and_accepts_none():
    assert scale_sweep.parse_scale_ms_overrides(None) is None
    assert scale_sweep.parse_scale_ms_overrides(
        ["50000=5000", "100000=20000"]) == {50_000: 5_000.0, 100_000: 20_000.0}


def test_evaluate_measurement_passes_through_diagnostic_keys_without_affecting_pass_fail():
    measurement = {
        "point_list_ms": 100.0, "accepted": 1, "rejected": 0,
        "expected_accepted": 1, "expected_rejected": 0, "lake_rows": 1, "flush_ms": 0.0,
        "point_list_warm_ms": 90.0, "point_list_concurrent_p95_ms": 250.0,
    }

    result = scale_sweep._evaluate_measurement(measurement, 2_000, scale_sweep.Thresholds())

    assert result["passed"] is True
    assert result["metrics"]["point_list_warm_ms"] == 90.0
    assert result["metrics"]["point_list_concurrent_p95_ms"] == 250.0


def test_stage_measure_diagnostics_are_opt_in_and_add_warm_and_concurrent_metrics():
    stage = load_stage_module()
    topology = [
        {"point_id": "p1", "building_id": "b1", "gateway_id": "g1"},
        {"point_id": "p2", "building_id": "b2", "gateway_id": "g2"},
    ]

    class Boundary:
        calls = 0

        def seed(self, points): pass
        def refresh_services(self): pass

        def point_list_milliseconds(self, gateways):
            self.calls += 1
            return [10.0, 20.0] if self.calls == 1 else [5.0, 8.0]

        def point_list_milliseconds_concurrent(self, gateways):
            return [1.0, 2.0, 3.0, 4.0]

        def ingest(self, frames):
            return 0 if frames and frames[0][1].startswith("unknown-") else len(frames)

        def lake_rows(self, buildings): return 2
        def cleanup(self): pass
        def wait(self, seconds): pass

    default_result = stage.measure(
        topology, Boundary(), invalid_per_gateway=1, flush_timeout_s=5, poll_interval_s=0.01)
    assert "point_list_warm_ms" not in default_result

    diagnostic_result = stage.measure(
        topology, Boundary(), invalid_per_gateway=1, flush_timeout_s=5, poll_interval_s=0.01,
        include_diagnostics=True)

    assert diagnostic_result["point_list_ms"] == 20.0
    assert diagnostic_result["point_list_warm_ms"] == 8.0
    assert diagnostic_result["point_list_concurrent_p50_ms"] == 2.0
    assert diagnostic_result["point_list_concurrent_max_ms"] == 4.0


def test_percentile_summary_uses_nearest_rank_on_sorted_values():
    stage = load_stage_module()

    summary = stage._percentile_summary("x", [40.0, 10.0, 30.0, 20.0])

    assert summary == {
        "x_p50_ms": 20.0, "x_p95_ms": 40.0, "x_max_ms": 40.0, "x_min_ms": 10.0,
    }
