import importlib.util
from pathlib import Path
import sys


MODULE_PATH = Path(__file__).parents[1] / "lake_retention_kpi.py"
SPEC = importlib.util.spec_from_file_location("lake_retention_kpi", MODULE_PATH)
lake_retention_kpi = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = lake_retention_kpi
SPEC.loader.exec_module(lake_retention_kpi)


def test_classify_retention_boundary_treats_the_boundary_itself_as_expired():
    # S3/MinIO ILM Expiration.Days=N acts once an object's age reaches N days — never strictly
    # after — so the boundary sample itself must classify as "expired", not "retained".
    assert lake_retention_kpi.classify_retention_boundary(23.9, retention_days=1) == "retained"
    assert lake_retention_kpi.classify_retention_boundary(24.0, retention_days=1) == "expired"
    assert lake_retention_kpi.classify_retention_boundary(47.9, retention_days=2) == "retained"
    assert lake_retention_kpi.classify_retention_boundary(48.0, retention_days=2) == "expired"


def test_retention_boundary_report_counts_leaks_and_premature_deletions():
    observations = [
        {"key": "a", "age_hours": 1.0, "present": True},   # inside window, present — correct
        {"key": "b", "age_hours": 30.0, "present": False},  # outside window, gone — correct
        {"key": "c", "age_hours": 30.0, "present": True},   # outside window but still present — leak
        {"key": "d", "age_hours": 1.0, "present": False},   # inside window but missing — premature
    ]

    report = lake_retention_kpi.retention_boundary_report(observations, retention_days=1)

    assert report["total"] == 4
    assert report["correct"] == 2
    assert report["retention_boundary_correct_ratio"] == 0.5
    assert report["expired_but_present"] == 1
    assert report["retained_but_missing"] == 1


def test_retention_boundary_report_all_correct_is_ratio_one():
    observations = [
        {"key": "a", "age_hours": 1.0, "present": True},
        {"key": "b", "age_hours": 30.0, "present": False},
    ]

    report = lake_retention_kpi.retention_boundary_report(observations, retention_days=1)

    assert report["retention_boundary_correct_ratio"] == 1.0
    assert report["expired_but_present"] == 0
    assert report["retained_but_missing"] == 0


def test_retention_boundary_report_empty_observations_reports_none_ratio():
    report = lake_retention_kpi.retention_boundary_report([], retention_days=1)

    assert report["total"] == 0
    assert report["retention_boundary_correct_ratio"] is None


def test_percentile_summary_uses_nearest_rank_on_sorted_values():
    summary = lake_retention_kpi.percentile_summary("flush_latency", [40.0, 10.0, 30.0, 20.0])

    assert summary == {
        "flush_latency_p50_ms": 20.0, "flush_latency_p95_ms": 40.0,
        "flush_latency_max_ms": 40.0, "flush_latency_min_ms": 10.0,
    }


def test_percentile_summary_empty_values_is_all_zero():
    summary = lake_retention_kpi.percentile_summary("compaction_latency", [])

    assert summary == {
        "compaction_latency_p50_ms": 0.0, "compaction_latency_p95_ms": 0.0,
        "compaction_latency_max_ms": 0.0, "compaction_latency_min_ms": 0.0,
    }


def test_success_rate_handles_mixed_and_empty():
    assert lake_retention_kpi.success_rate([{"success": True}, {"success": False}]) == 0.5
    assert lake_retention_kpi.success_rate([]) is None


def test_aggregate_cycle_kpis_only_averages_duration_over_successful_events():
    # A failed cycle's duration is not comparable to the latency threshold (a fast failure must
    # not understate p95) — the aggregator drops failed events from the latency percentiles while
    # still counting them in success_rate/count.
    events = [
        {"success": True, "duration_ms": 100.0},
        {"success": True, "duration_ms": 300.0},
        {"success": False, "duration_ms": 5.0},
    ]

    metrics = lake_retention_kpi.aggregate_cycle_kpis("flush", events)

    assert metrics["flush_count"] == 3
    assert metrics["flush_success_rate"] == round(2 / 3, 6)
    assert metrics["flush_latency_p50_ms"] == 100.0
    assert metrics["flush_latency_max_ms"] == 300.0


def test_aggregate_cycle_kpis_empty_events_reports_none_success_rate():
    metrics = lake_retention_kpi.aggregate_cycle_kpis("compaction", [])

    assert metrics["compaction_count"] == 0
    assert metrics["compaction_success_rate"] is None
    assert metrics["compaction_latency_p95_ms"] == 0.0


def test_objects_per_building_hour_groups_by_partition_prefix():
    keys = [
        "building_id=b1/year=2026/month=09/day=01/hour=00/part-1-2.parquet",
        "building_id=b1/year=2026/month=09/day=01/hour=00/part-3-4.parquet",
        "building_id=b1/year=2026/month=09/day=01/hour=01/compact-2026090101.parquet",
        "building_id=b2/year=2026/month=09/day=01/hour=00/part-1-2.parquet",
        "not-a-parquet-file.txt",
    ]

    counts = lake_retention_kpi.objects_per_building_hour(keys)

    assert counts["building_id=b1/year=2026/month=09/day=01/hour=00/"] == 2
    assert counts["building_id=b1/year=2026/month=09/day=01/hour=01/"] == 1
    assert counts["building_id=b2/year=2026/month=09/day=01/hour=00/"] == 1
    assert sum(counts.values()) == 4


def test_max_objects_per_building_hour_empty_is_zero():
    assert lake_retention_kpi.max_objects_per_building_hour([]) == 0
