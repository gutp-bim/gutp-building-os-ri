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
