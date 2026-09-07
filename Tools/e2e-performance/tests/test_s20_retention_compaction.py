"""Pure-logic coverage for the E11 capped-run harness (#263). Only the parts s20 can compute with
no docker/gRPC/MinIO access: interval derivation, the synthetic "already-settled hour" timestamp
placement that lets a <10-minute run exercise CompactionPlanner without waiting on a real wall-clock
hour boundary, and turning a raw MinIO key listing into retention observations.
"""
from __future__ import annotations

import importlib.util
import subprocess
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path
from unittest import mock

MODULE_PATH = Path(__file__).parents[1] / "s20_retention_compaction.py"
SPEC = importlib.util.spec_from_file_location("s20_retention_compaction", MODULE_PATH)
s20 = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = s20
SPEC.loader.exec_module(s20)


def test_flush_interval_seconds_falls_back_to_app_default_when_unset_or_non_positive():
    assert s20.flush_interval_seconds({}) == s20.DEFAULT_FLUSH_INTERVAL_MIN * 60
    assert s20.flush_interval_seconds({"PARQUET_FLUSH_INTERVAL": "0"}) == s20.DEFAULT_FLUSH_INTERVAL_MIN * 60
    assert s20.flush_interval_seconds({"PARQUET_FLUSH_INTERVAL": "not-a-number"}) == s20.DEFAULT_FLUSH_INTERVAL_MIN * 60
    assert s20.flush_interval_seconds({"PARQUET_FLUSH_INTERVAL": "2"}) == 120


def test_compaction_interval_seconds_falls_back_to_app_default_when_unset():
    assert s20.compaction_interval_seconds({}) == s20.DEFAULT_COMPACTION_INTERVAL_MIN * 60
    assert s20.compaction_interval_seconds({"LAKE_COMPACTION_INTERVAL": "3"}) == 180


def test_wave_interval_exceeds_the_flush_interval_so_each_wave_gets_its_own_part_file():
    # If wave spacing were <= the flush interval, two waves could land in the same flush cycle and
    # the compaction KPI would never see more than one part to merge.
    interval = s20.wave_interval_seconds(flush_interval_s=60)
    assert interval > 60


def test_compaction_wait_covers_at_least_one_full_scan_cycle_plus_margin():
    wait = s20.compaction_wait_seconds(compaction_interval_s=60)
    assert wait > 60


def test_floor_to_hour_zeroes_minutes_seconds_and_microseconds():
    dt = datetime(2026, 9, 8, 14, 37, 22, 123456, tzinfo=timezone.utc)
    assert s20.floor_to_hour(dt) == datetime(2026, 9, 8, 14, 0, 0, tzinfo=timezone.utc)


def test_settled_target_hour_is_hours_back_from_the_floored_current_hour():
    now = datetime(2026, 9, 8, 14, 37, 0, tzinfo=timezone.utc)
    assert s20.settled_target_hour(now, hours_back=2) == datetime(2026, 9, 8, 12, 0, 0, tzinfo=timezone.utc)


def test_settled_target_hour_rejects_a_non_positive_lookback():
    import pytest

    with pytest.raises(ValueError):
        s20.settled_target_hour(datetime.now(timezone.utc), hours_back=0)


def test_spread_timestamps_stays_within_the_target_hour_and_is_strictly_increasing():
    target_hour = datetime(2026, 9, 8, 12, 0, 0, tzinfo=timezone.utc)
    timestamps = s20.spread_timestamps(target_hour, 5)

    parsed = [datetime.fromisoformat(t) for t in timestamps]
    assert len(parsed) == 5
    assert parsed == sorted(parsed)
    # timedelta, not .replace(hour=target_hour.hour + 1) — the latter raises ValueError whenever
    # target_hour.hour == 23 (hour=24 is not a valid datetime.replace value).
    assert all(target_hour <= p < target_hour + timedelta(hours=1) for p in parsed)


def test_spread_timestamps_stays_within_the_target_hour_across_a_day_rollover():
    # hour=23 is the edge case .replace(hour=target_hour.hour + 1) cannot express.
    target_hour = datetime(2026, 9, 8, 23, 0, 0, tzinfo=timezone.utc)
    timestamps = s20.spread_timestamps(target_hour, 5)

    parsed = [datetime.fromisoformat(t) for t in timestamps]
    assert all(target_hour <= p < target_hour + timedelta(hours=1) for p in parsed)


def test_partition_prefix_matches_the_production_lake_partition_key_layout():
    hour = datetime(2026, 9, 8, 12, 0, 0, tzinfo=timezone.utc)
    # Mirrors LakePartitionKey.HourPrefix (DotNet/.../ParquetLake/LakePartitionKey.cs):
    # "building_id={b}/year={Y}/month={MM}/day={DD}/hour={HH}/"
    assert s20.partition_prefix("b1", hour) == "building_id=b1/year=2026/month=09/day=08/hour=12/"


def test_retention_observations_reports_presence_per_building_from_a_raw_key_listing():
    target_hour = datetime(2026, 9, 8, 12, 0, 0, tzinfo=timezone.utc)
    now = datetime(2026, 9, 8, 13, 5, 0, tzinfo=timezone.utc)
    keys = [
        "building_id=b1/year=2026/month=09/day=08/hour=12/part-1-2.parquet",
        "building_id=b3/year=2026/month=09/day=08/hour=11/part-1-2.parquet",  # different hour, ignored
    ]

    observations = s20.retention_observations(keys, target_hour, now, ["b1", "b2"])

    by_building = {"b1" if "b1" in o["key"] else "b2": o for o in observations}
    assert by_building["b1"]["present"] is True
    assert by_building["b2"]["present"] is False
    # age measured from the target hour's end (13:00), 5 minutes before `now`
    assert abs(by_building["b1"]["age_hours"] - (5 / 60)) < 1e-9


def test_objects_after_compaction_success_when_only_a_single_compact_object_remains():
    settled = ["building_id=b1/year=2026/month=09/day=08/hour=12/compact-2026090812.parquet"]
    unsettled = [
        "building_id=b1/year=2026/month=09/day=08/hour=12/part-1-2.parquet",
        "building_id=b1/year=2026/month=09/day=08/hour=12/part-3-4.parquet",
    ]

    assert s20.compaction_converged(settled, "building_id=b1/year=2026/month=09/day=08/hour=12/") is True
    assert s20.compaction_converged(unsettled, "building_id=b1/year=2026/month=09/day=08/hour=12/") is False
    assert s20.compaction_converged([], "building_id=b1/year=2026/month=09/day=08/hour=12/") is False


def _fake_run(stdout: str = ""):
    result = mock.Mock()
    result.stdout = stdout
    return result


def test_resolve_minio_credentials_prefers_the_container_env_over_the_host_shell(monkeypatch):
    # The MinIO container gets MINIO_ROOT_USER/PASSWORD from Compose's own .env file — this
    # process's shell may never have them exported, so the host env must not win when the
    # container itself reports different creds (the #263 review's false-empty-listing bug).
    monkeypatch.delenv("MINIO_ROOT_USER", raising=False)
    monkeypatch.delenv("MINIO_ROOT_PASSWORD", raising=False)

    def fake_run(cmd, **kwargs):
        var = cmd[-1]
        return _fake_run({"MINIO_ROOT_USER": "custom-user\n",
                           "MINIO_ROOT_PASSWORD": "custom-pass\n"}[var])

    with mock.patch.object(s20.subprocess, "run", side_effect=fake_run):
        assert s20._resolve_minio_credentials("building-os.minio") == ("custom-user", "custom-pass")


def test_resolve_minio_credentials_falls_back_to_host_env_then_default(monkeypatch):
    monkeypatch.setenv("MINIO_ROOT_USER", "host-user")
    monkeypatch.delenv("MINIO_ROOT_PASSWORD", raising=False)

    with mock.patch.object(s20.subprocess, "run", side_effect=lambda *a, **k: _fake_run("")):
        assert s20._resolve_minio_credentials("building-os.minio") == ("host-user", "buildingos123")


def test_resolve_minio_credentials_container_lookup_failure_falls_through(monkeypatch):
    monkeypatch.delenv("MINIO_ROOT_USER", raising=False)
    monkeypatch.delenv("MINIO_ROOT_PASSWORD", raising=False)

    with mock.patch.object(s20.subprocess, "run", side_effect=subprocess.SubprocessError("boom")):
        assert s20._resolve_minio_credentials("building-os.minio") == ("buildingos", "buildingos123")


def test_check_ilm_rule_configures_its_own_mc_alias_without_a_prior_listing_call():
    # #263 review: check_ilm_rule must be self-contained — it must not assume list_lake_keys ran
    # first and already configured the `mc` alias.
    calls: list[list[str]] = []

    def fake_run(cmd, **kwargs):
        calls.append(cmd)
        if cmd[3:5] == ["mc", "ilm"]:
            return _fake_run('{"config": {"ID": "other-rule"}}\n')
        return _fake_run("")

    with mock.patch.object(s20.subprocess, "run", side_effect=fake_run):
        s20.check_ilm_rule("building-os.minio", "cold")

    alias_calls = [c for c in calls if c[3:5] == ["mc", "alias"]]
    assert alias_calls, "check_ilm_rule must configure the mc alias itself"
