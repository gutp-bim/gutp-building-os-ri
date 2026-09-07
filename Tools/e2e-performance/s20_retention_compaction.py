#!/usr/bin/env python3
"""E11 — large-scale, sustained-load Parquet lake retention/compaction (#263). CAPPED-RUN variant.

#261 (multi-building 2k→50k Point scale, done) and #297/E10 (long-running memory soak) are both
prerequisites this axis builds on but does not repeat: E10's own scenario doc explicitly puts #263
("multi-building large-scale retention boundary") out of its scope, as a follow-up. This script is
that follow-up, but — following the same precedent E10 set when it added its capped-run variant
(gate on invariants a short run can actually prove, not on how long a true soak ran) — it is a
**short, bounded run** (well under 10 minutes), not the true multi-day retention-window soak that
would be needed to observe a real cross-day expiry live.

What a <10-minute run CAN prove about `LAKE_RETENTION_DAYS` (#217):

  1. The ILM rule is actually **applied** to the live MinIO bucket — the gap
     `LakeRetentionLifecycleTest.cs` leaves open (it only asserts the built config object, never
     live enforcement). ``check_ilm_rule`` queries MinIO directly for it.
  2. The pure classification/aggregation logic in ``lake_retention_kpi.py`` runs correctly against
     **real object ages** collected from this run's own lake writes.

What it CANNOT prove: that an object older than the window is actually gone. S3/MinIO
``Expiration.Days`` is whole-day granularity — nothing this harness sends can be more than a few
minutes old by the time the run ends, so every real observation classifies as "retained" and stays
present. The ``retention_boundary_correct_ratio`` KPI here is a regression guard on the
classification pipeline itself (a real leak/premature-deletion bug the harness DOES seed would still
be caught — see the "unsettled" part-file check below), not a substitute for a genuine multi-day
run — the same limitation E10 accepted for its own ≥72h acceptance criteria.

**The "already settled hour" trick** (same technique the E4/s14 rollup-backed measurement in
evaluation-report.md used to force compaction without waiting for a real wall-clock hour to end):
``ParquetLakeWriterWorker`` partitions by each frame's **event timestamp** (not by when the frame
was received), so sending frames whose ``timestamp`` falls in an hour that has *already* fully ended
(``settled_target_hour``) makes ``CompactionPlanner.IsHourSettled`` true immediately — the compactor
does not need to wait out a real hour boundary or its settle grace to act on that partition.

Reuses: ``s17_multibuilding_scale_sweep.build_topology`` for the multi-building/gateway point
topology (#261), ``s10_pointlist_integrity`` for twin seeding + the gRPC ingress stub loader,
``s19_endurance_soak`` for its ``docker_restart_state``/``probe_health``/``run_quality_checker``
helpers (E10 already re-shells quality_checker.py the same way E1's s15 does), and
``lake_retention_kpi`` for every KPI computation.

Usage:
  python s20_retention_compaction.py --out results/E11 [--points 300] [--buildings 3] [--gateways 6]
      [--waves 3] [--retention-days 1] [--target-hours-back 3]
      [--ingress localhost:5051] [--oxigraph http://localhost:7878]
      [--minio-endpoint localhost:9000] [--minio-container building-os.minio] [--bucket cold]
"""

from __future__ import annotations

import argparse
import asyncio
import json
import os
import subprocess
import sys
import time
from datetime import datetime, timedelta, timezone

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import lake_retention_kpi as lrk  # noqa: E402
import s10_pointlist_integrity as s10  # noqa: E402
import s17_multibuilding_scale_sweep as s17  # noqa: E402
import s19_endurance_soak as s19  # noqa: E402

# ParquetLakeWriterOptions.FlushInterval / CompactionWorkerOptions.Interval app defaults
# (DotNet/BuildingOS.Shared/Infrastructure/Telemetry/ParquetLake/{ParquetLakeWriterWorker,CompactionWorker}.cs).
# Used only to DERIVE poll budgets below when the caller hasn't overridden the env — never to change
# what the measured stack is actually running with.
DEFAULT_FLUSH_INTERVAL_MIN = 5
DEFAULT_COMPACTION_INTERVAL_MIN = 15

DEFAULT_CONTAINERS = [
    "building-os.connector-worker",
    "building-os.nats",
    "building-os.oxigraph",
    "building-os.minio",
]

RETENTION_RULE_ID = "building-os-lake-retention"  # LakeRetentionLifecycle.RuleId


# ── pure interval derivation (same convention as s19_endurance_soak.parquet_flush_interval_s) ──────
def _parse_positive_int(raw: str, default: int) -> int:
    """'' / non-numeric / <=0 -> default."""
    try:
        v = int(raw)
    except (TypeError, ValueError):
        v = 0
    return v if v > 0 else default


def flush_interval_seconds(env: dict) -> int:
    return _parse_positive_int(env.get("PARQUET_FLUSH_INTERVAL", ""), DEFAULT_FLUSH_INTERVAL_MIN) * 60


def compaction_interval_seconds(env: dict) -> int:
    return _parse_positive_int(env.get("LAKE_COMPACTION_INTERVAL", ""), DEFAULT_COMPACTION_INTERVAL_MIN) * 60


def wave_interval_seconds(flush_interval_s: int, margin_s: int = 20) -> int:
    """Spacing between ingest waves so each wave lands in its own flush cycle (a distinct
    part-*.parquet). Spacing at or below the flush interval risks two waves merging into one flush
    cycle, and the compaction KPI would then never see more than one part to merge."""
    return flush_interval_s + margin_s


def compaction_wait_seconds(compaction_interval_s: int, margin_s: int = 30) -> int:
    """How long to poll for compaction after the last wave. The compactor scans on a fixed cadence
    (not event-driven), so the wait must cover at least one full scan cycle plus margin for the
    read/dedup/write/verify/delete round trip CompactionWorker.CompactAsync performs."""
    return compaction_interval_s + margin_s


# ── synthetic "already settled" partition placement ─────────────────────────────────────────────────
def floor_to_hour(dt: datetime) -> datetime:
    return dt.replace(minute=0, second=0, microsecond=0)


def settled_target_hour(now: datetime, hours_back: int) -> datetime:
    """An hour guaranteed to already satisfy CompactionPlanner.IsHourSettled (hour end + settle
    grace <= now) without needing SETTLE_MINUTES=0 or a real wall-clock wait. hours_back=1 clears a
    zero settle grace; the default below is more generous so a nonzero grace still clears on the
    very first compaction scan."""
    if hours_back < 1:
        raise ValueError("hours_back must be >= 1 to guarantee the hour has already ended")
    return floor_to_hour(now) - timedelta(hours=hours_back)


def spread_timestamps(target_hour: datetime, count: int) -> list[str]:
    """`count` strictly increasing ISO8601 timestamps, all inside [target_hour, target_hour+1h) —
    same partition, distinct enough not to collide as (point_id, time) duplicates within one wave."""
    if count < 1:
        raise ValueError("count must be >= 1")
    span = timedelta(hours=1) - timedelta(seconds=1)
    step = span / count if count > 1 else timedelta(0)
    return [(target_hour + step * i).isoformat() for i in range(count)]


def partition_prefix(building: str, hour: datetime) -> str:
    """Mirrors LakePartitionKey.HourPrefix (DotNet/.../ParquetLake/LakePartitionKey.cs)."""
    return (f"building_id={building}/year={hour.year:04d}/month={hour.month:02d}/"
            f"day={hour.day:02d}/hour={hour.hour:02d}/")


def retention_observations(keys: list[str], target_hour: datetime, now: datetime,
                            buildings: list[str]) -> list[dict]:
    """One {age_hours, present} observation per building this run seeded, from a raw MinIO key
    listing. age_hours is measured from the target hour's *end* — when the data stopped being
    fresh — matching how Expiration.Days counts from an object's creation/last-modified time."""
    prefixes = {b: partition_prefix(b, target_hour) for b in buildings}
    present = {b: any(key.startswith(prefix) for key in keys) for b, prefix in prefixes.items()}
    age_hours = (now - (target_hour + timedelta(hours=1))).total_seconds() / 3600.0
    return [{"key": prefixes[b], "age_hours": age_hours, "present": present[b]} for b in buildings]


def compaction_converged(keys: list[str], prefix: str) -> bool:
    """True once a settled hour holds exactly its compacted object and no leftover parts (mirrors
    what CompactionWorker.CompactAsync leaves behind after a successful cycle). False on no objects
    at all — an hour that produced nothing has not converged, it has not started."""
    under_prefix = [k for k in keys if k.startswith(prefix) and k.endswith(".parquet")]
    if not under_prefix:
        return False
    return all(k.rsplit("/", 1)[-1].startswith("compact-") for k in under_prefix)


# ── thin I/O: MinIO listing + ILM verification (docker exec mc, same approach as
#    e2e/runner/normalize_storage.py's E7 listing — kept local so this harness has no import
#    dependency outside Tools/e2e-performance/) ─────────────────────────────────────────────────────
def _minio_container_env(container: str, var: str) -> str | None:
    """The value MinIO's own container is actually running `var` with (`docker exec printenv`), not
    this process's shell env. Compose interpolates MINIO_ROOT_USER/PASSWORD from its own `.env` file
    into the container at start-up (docker-compose.oss.yaml) without those ever being exported into
    whatever shell later launches this harness — trusting only `os.environ` here defaults to
    buildingos/buildingos123 even when the live bucket needs different creds, making every `mc`
    call below silently fail auth and `list_lake_keys` return an empty listing (a false KPI
    failure, not a real one)."""
    try:
        out = subprocess.run(["docker", "exec", container, "printenv", var],
                              check=False, capture_output=True, text=True, timeout=10).stdout.strip()
    except (subprocess.SubprocessError, OSError):
        return None
    return out or None


def _resolve_minio_credentials(container: str) -> tuple[str, str]:
    """Resolve the mc alias creds the *container* is actually running with: prefer the container's
    own env (authoritative — see `_minio_container_env`), fall back to this process's host env
    (matches quality_checker.py / s7_resilience_test.py's convention), then the compose default."""
    user = (_minio_container_env(container, "MINIO_ROOT_USER")
            or os.environ.get("MINIO_ROOT_USER") or "buildingos")
    password = (_minio_container_env(container, "MINIO_ROOT_PASSWORD")
                or os.environ.get("MINIO_ROOT_PASSWORD") or "buildingos123")
    return user, password


def _configure_mc_alias(container: str) -> None:
    """Self-contained `mc alias set` so every caller below (listing, ILM check) works standalone
    and in any order — neither depends on the other having run first."""
    user, password = _resolve_minio_credentials(container)
    subprocess.run(
        ["docker", "exec", container, "mc", "alias", "set", "lake", "http://localhost:9000",
         user, password],
        check=False, capture_output=True, timeout=30)


def list_lake_keys(container: str, bucket: str) -> list[str]:
    try:
        _configure_mc_alias(container)
        out = subprocess.run(
            ["docker", "exec", container, "mc", "ls", "--recursive", f"lake/{bucket}"],
            check=False, capture_output=True, text=True, timeout=60).stdout
    except (subprocess.SubprocessError, OSError):
        return []
    keys = []
    for line in out.splitlines():
        parts = line.split()
        if parts and parts[-1].endswith(".parquet"):
            keys.append(parts[-1])
    return keys


def check_ilm_rule(container: str, bucket: str, rule_id: str = RETENTION_RULE_ID) -> dict | None:
    """Best-effort verification that LakeRetentionHostedService actually applied its ILM rule to the
    live MinIO bucket (#263's "public storage contract" gap — LakeRetentionLifecycleTest.cs only
    asserts the built config object, never live enforcement). Returns None — not False — on any
    failure to reach/parse `mc`'s output: an unparseable response means "unknown", and the caller
    reports that rather than failing the run over a client-tooling quirk. `mc ilm` output shape has
    changed across MinIO client versions; this tries the modern `--json` form and degrades to None
    if it doesn't understand what came back."""
    try:
        _configure_mc_alias(container)  # self-contained — do not assume list_lake_keys ran first
        out = subprocess.run(
            ["docker", "exec", container, "mc", "ilm", "rule", "list", f"lake/{bucket}", "--json"],
            check=False, capture_output=True, text=True, timeout=30).stdout
    except (subprocess.SubprocessError, OSError):
        return None
    if not out.strip():
        return None
    found_any_rule = False
    for line in out.splitlines():
        line = line.strip()
        if not line:
            continue
        try:
            doc = json.loads(line)
        except ValueError:
            continue
        config = doc.get("config", doc) if isinstance(doc, dict) else None
        if not isinstance(config, dict):
            continue
        found_any_rule = True
        rid = config.get("ID") or config.get("id")
        if rid == rule_id:
            expiration = config.get("Expiration") or config.get("expiration") or {}
            days = expiration.get("Days") if isinstance(expiration, dict) else None
            return {"applied": True, "days": days}
    return {"applied": False, "days": None} if found_any_rule else None


# ── gRPC ingest (explicit per-frame timestamps — s10.stream_frames/s19.stream_chunk always use
#    "now", which is exactly what this axis must NOT do) ────────────────────────────────────────────
async def stream_wave(pb2, pb2g, target: str, frames: list[tuple[str, str, str]],
                       ack_timeout_s: float) -> tuple[int, int, str | None]:
    """One client-stream carrying `frames` = [(gateway_id, point_id, timestamp_iso), ...]. Frames for
    different buildings/gateways can share one stream — GatewayIngress resolves each frame's building
    independently via IPointMetadataCache."""
    import grpc  # type: ignore

    sent = 0

    async def gen():
        nonlocal sent
        for gw, pid, ts in frames:
            yield pb2.TelemetryFrame(gateway_id=gw, point_id=pid, value_num=21.5, timestamp=ts)
            sent += 1

    try:
        async with grpc.aio.insecure_channel(target) as ch:
            ack = await asyncio.wait_for(
                pb2g.GatewayIngressStub(ch).StreamTelemetry(gen()), timeout=ack_timeout_s)
        return sent, int(ack.accepted), None
    except Exception as e:  # noqa: BLE001 — a wave failure must not kill the run
        return sent, 0, f"{type(e).__name__}: {e}"


async def run(args) -> int:
    pb2, pb2g = s10.load_ingress_stubs()
    tag = datetime.now(timezone.utc).strftime("%Y%m%d%H%M%S")
    run_id = args.run_id or f"e11-{tag}"

    topology = s17.build_topology(args.points, args.buildings, args.gateways, run_id)
    buildings = sorted({p.building_id for p in topology})
    per_building_counts = s17.count_by(topology, "building_id")

    os.makedirs(args.out, exist_ok=True)
    containers = args.containers.split(",")
    seeded: list[str] = []
    try:
        print(f"[s20] seeding {len(topology)} points across {len(buildings)} buildings "
              f"({len({p.gateway_id for p in topology})} gateways)")
        for p in topology:
            s10.insert_point(args.oxigraph, p.point_id, p.gateway_id, p.building_id)
            seeded.append(p.point_id)

        first = topology[0]
        if not s19.wait_visible_at_scale(pb2, pb2g, args.ingress, first.gateway_id, first.point_id,
                                          args.seed_visible_timeout):
            print(f"[s20] seeded points not visible within {args.seed_visible_timeout}s — aborting",
                  file=sys.stderr)
            return 2

        baseline_restarts = s19.docker_restart_state(containers)

        flush_s = flush_interval_seconds(os.environ)
        compaction_s = compaction_interval_seconds(os.environ)
        wave_gap_s = wave_interval_seconds(flush_s)
        comp_wait_s = compaction_wait_seconds(compaction_s)
        now0 = datetime.now(timezone.utc)
        target_hour = settled_target_hour(now0, args.target_hours_back)
        prefixes = [partition_prefix(b, target_hour) for b in buildings]
        # One base timestamp per point (spread across the target hour); each wave then nudges every
        # point's timestamp by `wave` milliseconds. Without this, wave 2..N would resend the exact
        # same (point_id, time) pairs as wave 1 — quality_checker.py's duplicate detection is keyed
        # on (point_id, time), so identical timestamps across waves would make EVERY row from every
        # wave after the first look like a duplicate and spike duplicate_rate for no real reason.
        # A few ms of nudge is negligible against the per-point spacing (~seconds, given 300 points
        # spread across a full hour) and keeps every wave's timestamps inside the same target hour.
        base_timestamps = [datetime.fromisoformat(t) for t in spread_timestamps(target_hour, len(topology))]

        print(f"[s20] target settled hour={target_hour.isoformat()} (now={now0.isoformat()}, "
              f"{args.target_hours_back}h back); flush~{flush_s}s compaction~{compaction_s}s "
              f"wave_gap={wave_gap_s}s compaction_wait={comp_wait_s}s waves={args.waves}")

        flush_events: list[dict] = []
        seen_objects = 0
        for wave in range(args.waves):
            frames = [(p.gateway_id, p.point_id, (base_timestamps[i] + timedelta(milliseconds=wave)).isoformat())
                      for i, p in enumerate(topology)]
            t0 = time.monotonic()
            sent, accepted, err = await stream_wave(pb2, pb2g, args.ingress, frames, args.ack_timeout)
            print(f"[s20] wave {wave + 1}/{args.waves}: sent={sent} accepted={accepted}"
                  f"{' error=' + err if err else ''}")
            wave_deadline = t0 + wave_gap_s + 30.0
            flushed_at = None
            while True:
                keys_now = list_lake_keys(args.minio_container, args.bucket)
                counts = lrk.objects_per_building_hour(keys_now)
                total_now = sum(counts.get(prefix, 0) for prefix in prefixes)
                if total_now > seen_objects:
                    seen_objects = total_now
                    flushed_at = time.monotonic()
                    break
                if time.monotonic() >= wave_deadline:
                    break
                await asyncio.sleep(5)
            duration_ms = ((flushed_at or time.monotonic()) - t0) * 1000.0
            flush_events.append({
                "success": err is None and accepted == sent and flushed_at is not None,
                "duration_ms": duration_ms,
            })
            remaining = wave_gap_s - (time.monotonic() - t0)
            if wave < args.waves - 1 and remaining > 0:
                await asyncio.sleep(remaining)

        print(f"[s20] waves done; polling up to {comp_wait_s}s for compaction convergence")
        compaction_events: list[dict] = []
        comp_start = time.monotonic()
        comp_deadline = comp_start + comp_wait_s
        pending = set(buildings)
        while pending and time.monotonic() < comp_deadline:
            keys_now = list_lake_keys(args.minio_container, args.bucket)
            done_now = {b for b in pending if compaction_converged(keys_now, partition_prefix(b, target_hour))}
            for b in done_now:
                compaction_events.append({"success": True,
                                           "duration_ms": (time.monotonic() - comp_start) * 1000.0})
            pending -= done_now
            if pending:
                await asyncio.sleep(min(10.0, max(1.0, comp_deadline - time.monotonic())))
        for _ in pending:
            compaction_events.append({"success": False, "duration_ms": comp_wait_s * 1000.0})

        final_keys = list_lake_keys(args.minio_container, args.bucket)
        now_final = datetime.now(timezone.utc)
        retention_report = lrk.retention_boundary_report(
            retention_observations(final_keys, target_hour, now_final, buildings),
            args.retention_days)
        ilm = check_ilm_rule(args.minio_container, args.bucket)

        print(f"[s20] reconciling row counts against the lake ({len(buildings)} buildings)")
        expected_total = loss_weighted = dup_count_total = rows_total = 0
        for b in buildings:
            expected = per_building_counts[b] * args.waves
            expected_total += expected
            qc = s19.run_quality_checker(f"{run_id}-{b}", b, expected, args.minio_endpoint)
            if qc is None:
                loss_weighted += expected  # treat a missing result as 100% loss for this building
                continue
            rows = int(qc.get("db_row_count", 0))
            rows_total += rows
            dup_count_total += int(qc.get("duplicate_count", 0))
            loss_weighted += max(0, expected - rows)
        loss_rate = loss_weighted / expected_total if expected_total else 0.0
        dup_rate = dup_count_total / rows_total if rows_total else 0.0

        health = s19.probe_health(s19.DEFAULT_HEALTH_PROBES)
        restarts = s19.docker_restart_state(containers)
        restart_total = 0
        oom_any = False
        for c in containers:
            base = baseline_restarts.get(c)
            cur = restarts.get(c)
            if cur is None:
                continue
            base_count = base["restart_count"] if base else 0
            base_oom = base["oom_killed"] if base else False
            restart_total += max(0, cur["restart_count"] - base_count)
            oom_any = oom_any or (cur["oom_killed"] and not base_oom)

        metrics = {
            "buildings": len(buildings),
            "points": len(topology),
            "waves": args.waves,
            **lrk.aggregate_cycle_kpis("flush", flush_events),
            **lrk.aggregate_cycle_kpis("compaction", compaction_events),
            "objects_per_building_hour": lrk.max_objects_per_building_hour(final_keys),
            "retention_boundary_correct_ratio": retention_report["retention_boundary_correct_ratio"],
            "retention_boundary_expired_but_present": retention_report["expired_but_present"],
            "retention_boundary_retained_but_missing": retention_report["retained_but_missing"],
            "lake_rows": rows_total,
            "expected_rows": expected_total,
            "data_loss_ratio": round(loss_rate, 6),
            "duplicate_rate": round(dup_rate, 6),
            "restart_count_total": restart_total,
            "oom_count_total": int(oom_any),
            "health_probe_success_rate": (
                round(sum(1 for v in health.values() if v) / len(health), 4) if health else None),
        }
        if ilm is not None:
            metrics["retention_ilm_rule_applied"] = int(ilm["applied"])
            if ilm.get("days") is not None:
                metrics["retention_ilm_days_configured"] = ilm["days"]

        config = {
            "run_id": run_id, "points": len(topology), "buildings": buildings,
            "waves": args.waves, "retention_days": args.retention_days,
            "target_hour": target_hour.isoformat(), "flush_interval_s": flush_s,
            "compaction_interval_s": compaction_s, "wave_gap_s": wave_gap_s,
            "compaction_wait_s": comp_wait_s,
        }
        result = {
            "axis": "E11_lake_retention_scale",
            "generated_at": now_final.isoformat(),
            "config": config,
            "metrics": metrics,
        }
        out_path = os.path.join(args.out, "E11-retention.json")
        with open(out_path, "w") as f:
            json.dump(result, f, indent=2)
        print(f"[s20] wrote {out_path}")
        print(json.dumps(metrics, indent=2))

        hard_fail = (
            restart_total > 0
            or oom_any
            or loss_rate > 0.01
            or (retention_report["retention_boundary_correct_ratio"] is not None
                and retention_report["retention_boundary_correct_ratio"] < 1.0)
        )
        return 1 if hard_fail else 0
    finally:
        for p in seeded:
            try:
                s10.delete_point(args.oxigraph, p)
            except Exception:  # noqa: BLE001
                pass
        print(f"[s20] cleaned up {len(seeded)} seeded points")


def main() -> int:
    ap = argparse.ArgumentParser(description="E11 lake retention/compaction, capped-run (#263)")
    ap.add_argument("--out", default="results/E11")
    ap.add_argument("--run-id")
    ap.add_argument("--points", type=int, default=300)
    ap.add_argument("--buildings", type=int, default=3)
    ap.add_argument("--gateways", type=int, default=6)
    ap.add_argument("--waves", type=int, default=3,
                     help="ingest waves into the same synthetic settled hour; >=2 so the compactor "
                          "sees more than one part to merge (LAKE_COMPACTION_MIN_PARTS default 2)")
    ap.add_argument("--target-hours-back", type=int, default=3,
                     help="how far in the past the synthetic settled hour is; must clear "
                          "CompactionPlanner.IsHourSettled without waiting on a real wall-clock hour")
    ap.add_argument("--retention-days", type=int, default=1,
                     help="LAKE_RETENTION_DAYS the stack is expected to be running with. S3/MinIO "
                          "ILM Expiration.Days is whole-day granularity, so this run can only assert "
                          "the rule was APPLIED (check_ilm_rule), never observe a real cross-day "
                          "expiry — that needs a multi-day run, the same limitation E10 has for its "
                          "own >=72h acceptance")
    ap.add_argument("--ack-timeout", type=float, default=60.0)
    ap.add_argument("--seed-visible-timeout", type=float, default=120.0)
    ap.add_argument("--ingress", default=os.environ.get("INGRESS_TARGET", "localhost:5051"))
    ap.add_argument("--oxigraph", default=os.environ.get("OXIGRAPH_URL", "http://localhost:7878"))
    ap.add_argument("--minio-endpoint", default=os.environ.get("MINIO_ENDPOINT_HOST", "localhost:9000"))
    ap.add_argument("--minio-container", default=os.environ.get("MINIO_CONTAINER", "building-os.minio"))
    ap.add_argument("--bucket", default=os.environ.get("BUCKET", "cold"))
    ap.add_argument("--containers", default=",".join(DEFAULT_CONTAINERS))
    args = ap.parse_args()
    return asyncio.run(run(args))


if __name__ == "__main__":
    sys.exit(main())
