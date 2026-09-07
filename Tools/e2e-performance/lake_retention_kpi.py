#!/usr/bin/env python3
"""Pure KPI logic for E11 — large-scale, sustained-load Parquet lake retention/compaction (#263).

No docker/MinIO/NATS access here — this module only classifies and aggregates observations a
harness (s20_retention_compaction.py) collects elsewhere, so the KPI math itself stays unit-testable
without a live stack.

Two independent things this axis has to get right, split into two families of functions:

  1. **Retention boundary correctness** — for a given `LAKE_RETENTION_DAYS` (see
     ``LakeRetentionLifecycle.Build`` / ``LakeRetentionHostedService``,
     ``DotNet/BuildingOS.Shared/Infrastructure/Telemetry/ParquetLake/``), does an object's presence
     in the lake match what the configured window implies? ``classify_retention_boundary`` /
     ``retention_boundary_report``.
  2. **Flush/compaction success rate + latency** and **object-count-per-partition** under sustained
     load — ``aggregate_cycle_kpis`` (reuses the project's nearest-rank ``percentile_summary``
     convention from ``s17_scale_stage.py``) and ``objects_per_building_hour`` (the same
     partition-grouping ``normalize_storage.py``/``CompactionPlanner`` use for the E7 KPI, exposed
     here as a pure function so E11 can share it without shelling out to MinIO in a test).
"""

from __future__ import annotations

import math
import re


def _percentile(sorted_values: list[float], p: float) -> float:
    """Nearest-rank percentile; diagnostic reporting, not a statistical guarantee. Matches
    s17_scale_stage.py's convention so E11 latency KPIs read the same way as E1's."""
    if not sorted_values:
        return 0.0
    rank = max(0, min(len(sorted_values) - 1, math.ceil(p * len(sorted_values)) - 1))
    return sorted_values[rank]


def percentile_summary(prefix: str, values: list[float]) -> dict:
    sorted_values = sorted(values)
    return {
        f"{prefix}_p50_ms": round(_percentile(sorted_values, 0.50), 3),
        f"{prefix}_p95_ms": round(_percentile(sorted_values, 0.95), 3),
        f"{prefix}_max_ms": round(sorted_values[-1], 3) if sorted_values else 0.0,
        f"{prefix}_min_ms": round(sorted_values[0], 3) if sorted_values else 0.0,
    }


def success_rate(events: list[dict], key: str = "success") -> float | None:
    """None (not 0.0) when there were no events at all — a cycle that never ran is not a cycle
    that failed, and 0.0 would read as "ran and always failed" to the gate."""
    if not events:
        return None
    ok = sum(1 for e in events if e.get(key))
    return round(ok / len(events), 6)


def aggregate_cycle_kpis(prefix: str, events: list[dict], *, success_key: str = "success",
                          duration_key: str = "duration_ms") -> dict:
    """events: [{"success": bool, "duration_ms": float}, ...] — one entry per flush or compaction
    cycle observed during the run. Latency percentiles are computed over **successful** cycles
    only: a failed cycle's duration is not comparable to the latency threshold (a fast failure
    would silently pull p95 down and mask a real slow-cycle problem)."""
    metrics: dict = {
        f"{prefix}_success_rate": success_rate(events, success_key),
        f"{prefix}_count": len(events),
    }
    durations = [e[duration_key] for e in events if e.get(success_key) and duration_key in e]
    metrics.update(percentile_summary(f"{prefix}_latency", durations))
    return metrics


def classify_retention_boundary(age_hours: float, retention_days: float) -> str:
    """Classify an object's age against the retention-window boundary.

    Mirrors the S3/MinIO ILM semantics ``LakeRetentionLifecycle.Build()`` configures via
    ``Expiration.Days``: an object is expected to have expired once its age **reaches** the
    configured number of days — the boundary sample itself classifies as "expired", not
    "retained", since S3 lifecycle expiration acts at or after the Days threshold, never strictly
    after it.
    """
    return "expired" if age_hours >= retention_days * 24.0 else "retained"


def retention_boundary_report(observations: list[dict], retention_days: float) -> dict:
    """observations: [{"key": str, "age_hours": float, "present": bool}, ...].

    An object is "correct" when its actual presence in the lake matches what the retention window
    implies: still present while inside the window, gone once outside it. Splits the incorrect
    cases into the two failure modes that matter operationally:
      - ``expired_but_present``: a retention leak (the ILM rule did not clean it up).
      - ``retained_but_missing``: a premature deletion (data lost before its window closed).
    """
    if not observations:
        return {"total": 0, "correct": 0, "retention_boundary_correct_ratio": None,
                "expired_but_present": 0, "retained_but_missing": 0}

    correct = 0
    expired_but_present = 0
    retained_but_missing = 0
    for obs in observations:
        expected = classify_retention_boundary(obs["age_hours"], retention_days)
        expected_present = expected == "retained"
        present = bool(obs["present"])
        if expected_present == present:
            correct += 1
        elif expected == "expired" and present:
            expired_but_present += 1
        else:
            retained_but_missing += 1

    total = len(observations)
    return {
        "total": total,
        "correct": correct,
        "retention_boundary_correct_ratio": round(correct / total, 6),
        "expired_but_present": expired_but_present,
        "retained_but_missing": retained_but_missing,
    }


def objects_per_building_hour(keys: list[str]) -> dict[str, int]:
    """Groups Parquet object keys by their building-hour partition prefix (the directory the
    trailing ``/<file>.parquet`` is stripped from). Same grouping as
    ``normalize_storage.max_objects_per_building_hour`` / ``CompactionPlanner`` — kept here as a
    pure function of a key list so E11 can unit-test the compaction KPI without shelling out to
    ``mc`` inside the MinIO container."""
    counts: dict[str, int] = {}
    for key in keys:
        if not key.endswith(".parquet"):
            continue
        partition = re.sub(r"/[^/]+\.parquet$", "/", key)
        counts[partition] = counts.get(partition, 0) + 1
    return counts


def max_objects_per_building_hour(keys: list[str]) -> int:
    return max(objects_per_building_hour(keys).values(), default=0)
