#!/usr/bin/env python3
"""E12 — mixed-load benchmark: compaction-window ingress tail latency + control RTT with/without
concurrent telemetry load, WORKER_ROLE=all vs role-split (#401, child ② of the #399 Capability-based
Worker Runtime PRD).

WORKER_ROLE (#400/#401 parent) and docker-compose.roles.yaml (the role-split overlay) already exist
and are exercised in isolation by docs/reference/worker-role-split-measurement.md, which explicitly
names what is still missing: compaction-window-separated ingress percentiles, control RTT with vs
without concurrent telemetry, a CPU/memory/GC/consumer-lag time series, and a disclosed shared-host
latency confound (client and server compete for the same host's CPU). This script is that follow-up.

Reuses rather than reimplements:
  * ``s10_pointlist_integrity`` — gRPC ingress stub loader, twin seed/cleanup, seed-visibility wait.
  * ``s20_retention_compaction`` — the "already settled hour" technique
    (``settled_target_hour``/``spread_timestamps``/``partition_prefix``) to force a compaction cycle
    mid-run, and ``list_lake_keys``/``compaction_converged`` to detect when it finishes.
  * ``s19_endurance_soak`` — ``docker_stats``/``docker_restart_state`` (RSS + restart/OOM) sampling.
  * ``kpi_sampler`` — ``sample_pending`` (JetStream consumer/writer lag) and ``_slope`` (linear
    regression, reused here for the writer-lag trend #399's condition 3 asks for).
  * ``seed_twin_points.build_control_point_insert`` — the writable/controllable point S6's own
    seeding step uses.
  * ``k6/s6_point_control.js`` — the control-load generator (unmodified); run twice (baseline, then
    concurrently with sustained ingest) via its ``--out json=`` raw-sample dump so this script can
    rebucket ``control_submission_duration`` samples itself rather than only reading k6's own
    pre-aggregated percentiles.
  * ``mixed_load_kpi`` — all KPI math (compaction-window bucketing, RTT comparison, the #399
    split_decision judgment). This script's own job is orchestration + result shaping only.

Docker/subprocess orchestration is kept as thin wrapper functions around the pure helpers below (CLI
parsing, docker-compose service selection, docker-stats CPU% parsing/aggregation, k6 raw-metric
extraction, result-dict shaping, Markdown rendering) so those pure parts stay unit-testable with no
live stack — see ``tests/test_s21_mixed_load_benchmark.py``.

**Shared-host caveat (disclosed, not fixed here):** this harness runs its load generator (this
process, plus k6) on the same host as the measured stack, exactly the confound
worker-role-split-measurement.md already flagged for its own before/after numbers. Absolute latency
values from a single-host run are not a substitute for a dedicated-client benchmark; see
e2e/scenarios/E12-mixed-load-benchmark.md.

Usage:
  python s21_mixed_load_benchmark.py --out results/E12 --role-mode all
  python s21_mixed_load_benchmark.py --out results/E12 --role-mode split --duration-s 180
"""

from __future__ import annotations

import argparse
import asyncio
import json
import os
import re
import subprocess
import sys
import time
from datetime import datetime, timedelta, timezone

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import kpi_sampler as kpis  # noqa: E402
import mixed_load_kpi as mlk  # noqa: E402
import s10_pointlist_integrity as s10  # noqa: E402
import s19_endurance_soak as s19  # noqa: E402
import s20_retention_compaction as s20  # noqa: E402
import seed_twin_points as stp  # noqa: E402

DEFAULT_CONTAINERS = ["building-os.connector-worker", "building-os.nats"]

# Services every role_mode needs (API for the control path, NATS/OxiGraph/MinIO/Postgres for
# ingest+lake). building-os.gateway-bridge is NOT included: control here goes through the in-process
# ENABLE_SIM_CONTROL handler (NatsPointControlWorker), the same path s6_point_control.sh exercises.
BASE_SERVICES = [
    "building-os.nats", "building-os.oxigraph", "building-os.minio",
    "building-os.postgres", "building-os.pgbouncer", "building-os.pgbouncer-session",
    "building-os.api",
]


# ═══════════════════════════════════════════════════════════════════════════════════════════════════
# Pure helpers (unit-tested in tests/test_s21_mixed_load_benchmark.py — no docker/gRPC/k6 access)
# ═══════════════════════════════════════════════════════════════════════════════════════════════════

def build_arg_parser() -> argparse.ArgumentParser:
    ap = argparse.ArgumentParser(description="E12 mixed-load benchmark (#401)")
    ap.add_argument("--role-mode", choices=["all", "split"], default="all",
                     help="'all' = single WORKER_ROLE=all connector-worker (default OSS stack); "
                          "'split' = docker-compose.roles.yaml overlay (separate ingest/lake/control "
                          "processes)")
    ap.add_argument("--out", default="results/E12")
    ap.add_argument("--duration-s", type=int, default=300,
                     help="phase 2 duration: concurrent sustained ingest + control load")
    ap.add_argument("--ingest-rate", type=float, default=20.0, help="sustained ingest frames/sec")
    ap.add_argument("--ingest-points", type=int, default=50)
    ap.add_argument("--control-vus", type=int, default=3)
    ap.add_argument("--control-baseline-duration-s", type=int, default=45,
                     help="phase 1 duration: control load alone, no concurrent ingest")
    ap.add_argument("--control-point-id", default=os.environ.get("CONTROL_POINT_ID"))
    ap.add_argument("--target-hours-back", type=int, default=3,
                     help="s20 settled_target_hour technique: how far back the forced-compaction "
                          "wave's synthetic event timestamps land")
    ap.add_argument("--compaction-points", type=int, default=6,
                     help="distinct points used ONLY for the forced-compaction wave, kept separate "
                          "from the sustained ingest points so their partitions don't collide")
    ap.add_argument("--compaction-wait-s", type=int, default=180,
                     help="how long to poll for compaction_converged after the forced wave")
    ap.add_argument("--post-settle-margin-s", type=float, default=mlk.DEFAULT_POST_SETTLE_MARGIN_S,
                     help="mixed_load_kpi transition-zone margin after convergence")
    ap.add_argument("--sample-interval", type=int, default=5, help="resource sampling interval (s)")
    ap.add_argument("--ingress", default=os.environ.get("INGRESS_TARGET", "localhost:5051"))
    ap.add_argument("--oxigraph", default=os.environ.get("OXIGRAPH_URL", "http://localhost:7878"))
    ap.add_argument("--minio-endpoint", default=os.environ.get("MINIO_ENDPOINT_HOST", "localhost:9000"))
    ap.add_argument("--minio-container", default=os.environ.get("MINIO_CONTAINER", "building-os.minio"))
    ap.add_argument("--bucket", default=os.environ.get("BUCKET", "cold"))
    ap.add_argument("--base-url", default=os.environ.get("BASE_URL", "http://localhost:5000"))
    ap.add_argument("--compose-file", default=os.environ.get("COMPOSE_FILE", "docker-compose.oss.yaml"))
    ap.add_argument("--roles-compose-file",
                     default=os.environ.get("ROLES_COMPOSE_FILE", "docker-compose.roles.yaml"))
    ap.add_argument("--containers", default=",".join(DEFAULT_CONTAINERS))
    ap.add_argument("--skip-stack-up", action="store_true",
                     help="assume the stack is already up (bring-up handled by the caller)")
    ap.add_argument("--ingest-cpu-container",
                     default=os.environ.get("INGEST_CPU_CONTAINER", "building-os.connector-worker"),
                     help="container whose CPU% is sampled for #399 condition 1. In --role-mode "
                          "split this should be the ingest-role container (still named "
                          "building-os.connector-worker — the base service is demoted to WORKER_ROLE="
                          "ingest by docker-compose.roles.yaml, not renamed)")
    ap.add_argument("--ingest-cpu-threshold-pct", type=float, default=65.0,
                     help="CPU%% level #399 condition 1 calls 'high' (its own text: roughly 60-70%%)")
    ap.add_argument("--ingest-cpu-sustained-ratio-threshold", type=float, default=0.8,
                     help="fraction of resource samples that must be at/above --ingest-cpu-threshold-pct "
                          "for condition 1 to be met (mixed_load_kpi.split_decision)")
    ap.add_argument("--ingress-p95-degradation-threshold", type=float, default=1.5)
    ap.add_argument("--writer-lag-slope-threshold", type=float, default=1.0)
    ap.add_argument("--control-rtt-degradation-threshold", type=float, default=1.5)
    return ap


def parse_args(argv: list[str]) -> argparse.Namespace:
    return build_arg_parser().parse_args(argv)


def docker_compose_up_args(role_mode: str, compose_file: str, roles_compose_file: str) -> list[str]:
    """The ``docker compose ... up -d <services>`` argv for one role_mode. 'all' brings up the
    single WORKER_ROLE=all connector-worker; 'split' adds the docker-compose.roles.yaml overlay
    (which demotes that same service to WORKER_ROLE=ingest — see the file's own header comment) plus
    the two extra lake/control containers it defines."""
    if role_mode == "all":
        services = [*BASE_SERVICES, "building-os.connector-worker"]
        return ["docker", "compose", "-f", compose_file, "up", "-d", *services]
    if role_mode == "split":
        services = [*BASE_SERVICES, "building-os.connector-worker",
                    "building-os.connector-worker-lake", "building-os.connector-worker-control"]
        return ["docker", "compose", "-f", compose_file, "-f", roles_compose_file, "up", "-d", *services]
    raise ValueError(f"unknown role_mode: {role_mode!r} (expected 'all' or 'split')")


def parse_cpu_percent(text: str) -> float | None:
    """'12.34%' -> 12.34 (docker stats --format {{.CPUPerc}}). None if unparsable."""
    m = re.match(r"^([\d.]+)%$", (text or "").strip())
    if not m:
        return None
    try:
        return float(m.group(1))
    except ValueError:
        return None


def cpu_high_ratio(samples: list[float], threshold_pct: float = 65.0) -> float | None:
    """Fraction of CPU%% samples at/above `threshold_pct` — the #399 condition 1 metric
    (``ingest_cpu_high_ratio``). None (not 0.0) on no samples: absence of data must not read as
    "measured 0% high-CPU", which split_decision would otherwise score as a real "condition not met"
    instead of insufficient_data."""
    if not samples:
        return None
    high = sum(1 for v in samples if v >= threshold_pct)
    return round(high / len(samples), 6)


def parse_k6_json_metric_lines(lines: list[str], metric_name: str) -> list[float]:
    """Extract raw ``data.value`` samples for `metric_name` from k6's ``--out json=<file>`` line
    format (one JSON object per line; only ``type: "Point"`` rows carry a per-request value — the
    periodic ``type: "Metric"`` rows are metadata and skipped). Unparsable lines are skipped rather
    than raising, since a k6 run can be interrupted mid-write."""
    values: list[float] = []
    for line in lines:
        line = line.strip()
        if not line:
            continue
        try:
            doc = json.loads(line)
        except ValueError:
            continue
        if not isinstance(doc, dict) or doc.get("type") != "Point" or doc.get("metric") != metric_name:
            continue
        data = doc.get("data")
        val = data.get("value") if isinstance(data, dict) else None
        if isinstance(val, (int, float)):
            values.append(float(val))
    return values


def read_k6_metric_values(path: str, metric_name: str) -> list[float]:
    if not os.path.isfile(path):
        return []
    with open(path) as f:
        return parse_k6_json_metric_lines(f.readlines(), metric_name)


def build_kpi_summary(config: dict, ingress_metrics: dict, control_metrics: dict,
                       resource_metrics: dict, decision: dict) -> dict:
    """Shapes the three independently-computed metric dicts (ingress compaction-window comparison,
    control RTT comparison, resource/CPU/lag summary — disjoint key prefixes by construction, so a
    plain merge cannot silently drop anything) plus the #399 verdict into the canonical
    ``{axis, metrics}`` envelope ``e2e/runner/gate.py`` expects."""
    return {
        "axis": "E12_mixed_load_benchmark",
        "generated_at": datetime.now(timezone.utc).isoformat(),
        "config": config,
        "metrics": {**ingress_metrics, **control_metrics, **resource_metrics},
        "split_decision": decision,
    }


def render_report_md(result: dict) -> str:
    config = result.get("config", {})
    metrics = result.get("metrics", {})
    decision = result.get("split_decision", {})
    lines = [
        "# E12 — Mixed-load benchmark (#401)",
        "",
        f"generated_at: {result.get('generated_at', '')}  ",
        f"role_mode: **{config.get('role_mode', '?')}**",
        "",
        "## Run conditions",
        "",
        "| key | value |",
        "|---|---|",
    ]
    for k in sorted(config):
        lines.append(f"| {k} | {config[k]} |")
    lines += ["", "## Key metrics", "", "| metric | value |", "|---|--:|"]
    for k in sorted(metrics):
        lines.append(f"| {k} | {metrics[k]} |")
    lines += [
        "", "## #399 split-decision verdict", "",
        f"**split_recommended: {decision.get('split_recommended')}**  ",
        f"{decision.get('rationale', '')}",
        "",
        "| condition | status | met | value | threshold |",
        "|---|---|---|--:|--:|",
    ]
    for key in sorted(decision.get("conditions", {})):
        cond = decision["conditions"][key]
        lines.append(f"| {key} | {cond.get('status')} | {cond.get('met')} | "
                      f"{cond.get('value')} | {cond.get('threshold')} |")
        if cond.get("note"):
            lines.append(f"| | note: {cond['note']} | | | |")
    lines.append("")
    return "\n".join(lines)


# ═══════════════════════════════════════════════════════════════════════════════════════════════════
# Thin I/O (subprocess/docker/k6/gRPC/NATS) — not unit-tested; delegates all math to mixed_load_kpi
# and the pure helpers above.
# ═══════════════════════════════════════════════════════════════════════════════════════════════════

def docker_stats_cpu(containers: list[str]) -> dict[str, float]:
    """One `docker stats --no-stream` call -> {container: cpu_pct}, mirroring
    s19_endurance_soak.docker_stats's shape but for CPU% instead of memory."""
    try:
        out = subprocess.run(
            ["docker", "stats", "--no-stream", "--format", "{{.Name}}\t{{.CPUPerc}}"],
            capture_output=True, text=True, timeout=15, check=True,
        ).stdout
    except (subprocess.SubprocessError, OSError):
        return {}
    want = set(containers)
    result: dict[str, float] = {}
    for line in out.splitlines():
        parts = line.split("\t")
        if len(parts) != 2 or parts[0] not in want:
            continue
        pct = parse_cpu_percent(parts[1])
        if pct is not None:
            result[parts[0]] = pct
    return result


def run_k6_control(args: argparse.Namespace, control_point_id: str, run_id: str, duration_s: int) -> str:
    """Runs k6/s6_point_control.js unmodified for `duration_s` seconds, dumping every raw metric
    point via ``--out json=`` so this harness can rebucket ``control_submission_duration`` itself
    (k6's own ``--summary-export`` only carries pre-aggregated percentiles, not per-request samples).
    Returns the path to that raw-json file (may not exist if k6 itself failed to start)."""
    perf = os.path.dirname(os.path.abspath(__file__))
    out_dir = os.path.join(perf, "results", run_id)
    os.makedirs(out_dir, exist_ok=True)
    json_path = os.path.join(out_dir, "k6-raw.json")
    summary_path = os.path.join(out_dir, "k6-summary.json")
    cmd = [
        "k6", "run",
        "--env", f"BASE_URL={args.base_url}",
        "--env", f"CONTROL_POINT_ID={control_point_id}",
        "--env", f"VUS={args.control_vus}",
        "--env", f"DURATION={duration_s}s",
        "--env", f"TEST_RUN_ID={run_id}",
        "--summary-export", summary_path,
        "--out", f"json={json_path}",
        os.path.join(perf, "k6", "s6_point_control.js"),
    ]
    print(f"[s21][control] {' '.join(cmd)}")
    try:
        subprocess.run(cmd, check=False, capture_output=True, text=True, timeout=duration_s + 90)
    except subprocess.TimeoutExpired:
        print("[s21][control] k6 run timed out", file=sys.stderr)
    return json_path


def delete_control_point(oxigraph: str, point_id: str) -> None:
    """Cleans up the writable/controllable point seed_twin_points.build_control_point_insert wrote
    (`urn:perf:ctlpt:{id}` / `urn:perf:ctldev:{id}` — a different URI scheme from
    s10_pointlist_integrity.insert_point's `urn:perf:ingpt:{id}` / `urn:perf:ingdev:{id}`, so
    s10.delete_point would silently no-op on this point rather than remove it)."""
    pt, dev = f"urn:perf:ctlpt:{point_id}", f"urn:perf:ctldev:{point_id}"
    s10.sparql_update(oxigraph, f"DELETE WHERE {{ <{pt}> ?p ?o }};\nDELETE WHERE {{ <{dev}> ?p ?o }}")


def _parse_iso_epoch(raw: str | None) -> float | None:
    if not raw:
        return None
    try:
        return datetime.fromisoformat(raw).astimezone(timezone.utc).timestamp()
    except (ValueError, TypeError):
        return None


def _extract_rows(data: object) -> list[dict]:
    if isinstance(data, dict):
        items = data.get("telemetries")
        return items if isinstance(items, list) else [data]
    return data if isinstance(data, list) else []


async def run_ingest_with_latency_sampling(pb2, pb2g, target: str, gw: str, building: str,
                                            points: list[str], rate: float, duration_s: int,
                                            epoch: float) -> list[dict]:
    """Sustained gRPC ingest for `duration_s` at ~`rate`/s, subscribed the same way
    s11_ingest_latency.py does (core-NATS on ``building-os.validated.telemetry``) so every accepted
    frame's ingest E2E latency is recorded — but tagged with `elapsed_s` (relative to `epoch`, the
    phase-2 start) rather than only aggregated at the end, so mixed_load_kpi can bucket samples by
    whether they landed before/after the forced compaction cycle converged."""
    import grpc  # type: ignore
    import nats  # type: ignore

    samples: list[dict] = []
    interval = 1.0 / rate if rate > 0 else 0.0
    nc = await nats.connect(os.environ.get("NATS_URL", "nats://localhost:4222"))

    async def on_msg(msg):
        recv = time.time()
        try:
            doc = json.loads(msg.data)
        except ValueError:
            return
        for row in _extract_rows(doc):
            if not isinstance(row, dict) or row.get("building") != building:
                continue
            gen = _parse_iso_epoch(row.get("datetime"))
            if gen is None:
                continue
            samples.append({"elapsed_s": round(time.monotonic() - epoch, 3),
                             "latency_ms": (recv - gen) * 1000.0})

    sub = await nc.subscribe("building-os.validated.telemetry", cb=on_msg)
    total = max(1, round(rate * duration_s))

    async def gen():
        for i in range(total):
            p = points[i % len(points)]
            yield pb2.TelemetryFrame(gateway_id=gw, point_id=p, value_num=20.0 + (i % 100) / 10.0,
                                      timestamp=datetime.now(timezone.utc).isoformat())
            if interval:
                await asyncio.sleep(interval)

    try:
        async with grpc.aio.insecure_channel(target) as ch:
            await asyncio.wait_for(pb2g.GatewayIngressStub(ch).StreamTelemetry(gen()),
                                    timeout=duration_s + max(30.0, duration_s * 0.2))
    except Exception as e:  # noqa: BLE001 — a stream failure must not kill the benchmark
        print(f"[s21][ingest] stream error: {type(e).__name__}: {e}", file=sys.stderr)

    await asyncio.sleep(5.0)  # drain grace so late messages land
    await sub.unsubscribe()
    await nc.drain()
    return samples


async def force_compaction_and_wait(args: argparse.Namespace, building: str, points: list[str],
                                     gw: str, epoch: float) -> tuple[float | None, dict]:
    """Forces a compaction cycle mid-run using s20_retention_compaction's settled-hour technique, on
    `points`/`building` kept distinct from the sustained-ingest partitions (so the ingest KPI's own
    flush activity is never confused with this forced cycle). Sends 2 waves (>= the app's
    LAKE_COMPACTION_MIN_PARTS default of 2, same reasoning as s20's own --waves >= 2) into an
    already-settled hour, then polls MinIO for compaction_converged. Returns (converged_at_s, meta):
    converged_at_s is the wall-clock elapsed second (relative to `epoch`) convergence first flipped
    true, or None if it never did within --compaction-wait-s (mixed_load_kpi then conservatively
    classifies every ingest sample as "during" rather than a false "not_during" baseline)."""
    pb2, pb2g = s10.load_ingress_stubs()
    now0 = datetime.now(timezone.utc)
    target_hour = s20.settled_target_hour(now0, args.target_hours_back)
    prefix = s20.partition_prefix(building, target_hour)
    base_timestamps = [datetime.fromisoformat(t) for t in s20.spread_timestamps(target_hour, len(points))]

    total_sent = total_accepted = 0
    for wave in range(2):
        frames = [(gw, p, (base_timestamps[i] + timedelta(milliseconds=wave)).isoformat())
                  for i, p in enumerate(points)]
        sent, accepted, err = await s20.stream_wave(pb2, pb2g, args.ingress, frames, ack_timeout_s=60.0)
        total_sent += sent
        total_accepted += accepted
        print(f"[s21][compaction] wave {wave + 1}/2: sent={sent} accepted={accepted}"
              f"{' error=' + err if err else ''}")
        await asyncio.sleep(2.0)

    deadline = time.monotonic() + args.compaction_wait_s
    converged_at_s = None
    while time.monotonic() < deadline:
        keys = s20.list_lake_keys(args.minio_container, args.bucket)
        if s20.compaction_converged(keys, prefix):
            converged_at_s = round(time.monotonic() - epoch, 1)
            break
        await asyncio.sleep(5.0)
    meta = {"sent": total_sent, "accepted": total_accepted, "target_hour": target_hour.isoformat(),
            "prefix": prefix, "converged": converged_at_s is not None}
    return converged_at_s, meta


async def run(args: argparse.Namespace) -> int:
    pb2, pb2g = s10.load_ingress_stubs()
    tag = datetime.now(timezone.utc).strftime("%Y%m%d%H%M%S")
    run_id = f"e12-{tag}"
    os.makedirs(args.out, exist_ok=True)

    gw = f"GW-E12-{tag}"
    building = run_id
    ingest_points = [f"e12-ing-{tag}-{i:04d}" for i in range(args.ingest_points)]
    compaction_points = [f"e12-cmp-{tag}-{i:03d}" for i in range(args.compaction_points)]
    control_point_id = args.control_point_id or f"e12-ctl-{tag}"
    control_gateway = f"GW-{control_point_id}"
    containers = args.containers.split(",")

    seeded: list[str] = []
    try:
        if not args.skip_stack_up:
            compose_argv = docker_compose_up_args(args.role_mode, args.compose_file, args.roles_compose_file)
            print(f"[s21] bringing up stack (role_mode={args.role_mode}): {' '.join(compose_argv)}")
            subprocess.run(compose_argv, check=False)
            print("[s21] waiting 15s for the stack to settle...")
            time.sleep(15)

        print(f"[s21] seeding {len(ingest_points)} ingest points + {len(compaction_points)} "
              f"compaction points + 1 control point (gw={gw}, building={building})")
        for p in ingest_points:
            s10.insert_point(args.oxigraph, p, gw, building)
            seeded.append(p)
        for p in compaction_points:
            s10.insert_point(args.oxigraph, p, gw, building)
            seeded.append(p)
        stp.post_update(args.oxigraph,
                         stp.build_control_point_insert(control_point_id, control_gateway, building))

        if not s10.wait_visible(pb2, pb2g, args.ingress, gw, ingest_points[0], timeout_s=120.0):
            print("[s21] seeded points not visible within timeout — aborting", file=sys.stderr)
            return 2

        baseline_restarts = s19.docker_restart_state(containers)

        print(f"[s21] phase 1: control baseline ({args.control_baseline_duration_s}s, no concurrent ingest)")
        baseline_json = run_k6_control(args, control_point_id, f"{run_id}-ctl-baseline",
                                        args.control_baseline_duration_s)
        baseline_rtt = read_k6_metric_values(baseline_json, "control_submission_duration")
        print(f"[s21] phase 1 done: {len(baseline_rtt)} baseline control samples")

        print(f"[s21] phase 2: concurrent ingest (~{args.ingest_rate}/s) + control load for "
              f"{args.duration_s}s, forcing a compaction cycle mid-run")
        phase2_epoch = time.monotonic()
        resource_samples: list[dict] = []
        stop_at = phase2_epoch + args.duration_s

        async def sample_resources_loop() -> None:
            while time.monotonic() < stop_at:
                try:
                    pending_total, _ = kpis.sample_pending(
                        os.environ.get("NATS_MONITOR_URL", "http://localhost:8222"), "VALIDATED")
                except Exception:  # noqa: BLE001 — a monitoring hiccup must not kill the benchmark
                    pending_total = -1
                resource_samples.append({
                    "elapsed_s": round(time.monotonic() - phase2_epoch, 1),
                    "mem_mib": s19.docker_stats(containers),
                    "cpu_pct": docker_stats_cpu(containers),
                    "consumer_pending_total": pending_total,
                })
                await asyncio.sleep(args.sample_interval)

        sampler_task = asyncio.create_task(sample_resources_loop())
        ingest_task = asyncio.create_task(run_ingest_with_latency_sampling(
            pb2, pb2g, args.ingress, gw, building, ingest_points, args.ingest_rate,
            args.duration_s, phase2_epoch))
        control_task = asyncio.create_task(asyncio.to_thread(
            run_k6_control, args, control_point_id, f"{run_id}-ctl-concurrent", args.duration_s))

        # Give ingest+control a head start before forcing compaction, so the "not_during" baseline
        # (well before the forced wave) and "during" bucket both have real concurrent-load samples.
        await asyncio.sleep(min(20.0, max(1.0, args.duration_s / 4)))
        converged_at_s, compaction_meta = await force_compaction_and_wait(
            args, building, compaction_points, gw, phase2_epoch)

        ingest_samples = await ingest_task
        await sampler_task
        concurrent_json = await control_task
        concurrent_rtt = read_k6_metric_values(concurrent_json, "control_submission_duration")
        print(f"[s21] phase 2 done: {len(ingest_samples)} ingest latency samples, "
              f"{len(concurrent_rtt)} concurrent control samples, "
              f"compaction_converged_at_s={converged_at_s}")

        cpu_samples = [t["cpu_pct"].get(args.ingest_cpu_container) for t in resource_samples]
        cpu_samples = [v for v in cpu_samples if v is not None]
        ingest_cpu_ratio = cpu_high_ratio(cpu_samples, args.ingest_cpu_threshold_pct)

        pend = [(t["elapsed_s"], t["consumer_pending_total"]) for t in resource_samples
                if t["consumer_pending_total"] >= 0]
        half = pend[len(pend) // 2:]
        pending_slope = round(kpis._slope([x for x, _ in half], [y for _, y in half]), 4) if len(half) >= 2 else None

        ingress_metrics = mlk.ingress_compaction_comparison(ingest_samples, converged_at_s,
                                                             args.post_settle_margin_s)
        control_metrics = mlk.control_rtt_load_comparison(baseline_rtt, concurrent_rtt)
        resource_metrics = {
            "ingest_cpu_high_ratio": ingest_cpu_ratio,
            "ingest_cpu_samples": len(cpu_samples),
            "consumer_pending_slope_per_sec": pending_slope,
            "consumer_pending_max": max((y for _, y in pend), default=None),
            "compaction_converged": compaction_meta["converged"],
            "compaction_converged_at_s": converged_at_s,
        }

        decision = mlk.split_decision(
            {
                "ingest_cpu_high_ratio": ingest_cpu_ratio,
                "ingest_e2e_compaction_p95_degradation_ratio":
                    ingress_metrics["ingest_e2e_compaction_p95_degradation_ratio"],
                "consumer_pending_slope_per_sec": pending_slope,
                "control_rtt_p95_degradation_ratio": control_metrics["control_rtt_p95_degradation_ratio"],
            },
            ingest_cpu_high_ratio_threshold=args.ingest_cpu_sustained_ratio_threshold,
            ingress_p95_degradation_ratio_threshold=args.ingress_p95_degradation_threshold,
            writer_lag_slope_threshold=args.writer_lag_slope_threshold,
            control_rtt_degradation_ratio_threshold=args.control_rtt_degradation_threshold,
        )

        config = {
            "run_id": run_id, "role_mode": args.role_mode,
            "duration_s": args.duration_s, "ingest_rate": args.ingest_rate,
            "ingest_points": len(ingest_points), "control_vus": args.control_vus,
            "control_baseline_duration_s": args.control_baseline_duration_s,
            "control_point_id": control_point_id,
            "target_hours_back": args.target_hours_back,
            "compaction_points": len(compaction_points),
            "compaction_wait_s": args.compaction_wait_s,
            "post_settle_margin_s": args.post_settle_margin_s,
            "sample_interval_s": args.sample_interval,
            "ingest_cpu_threshold_pct": args.ingest_cpu_threshold_pct,
            "shared_host_caveat": (
                "client (this process + k6) and server (ConnectorWorker/API/NATS/MinIO) run on the "
                "same host in this harness; absolute latency numbers are confounded by CPU contention "
                "between them — see docs/reference/worker-role-split-measurement.md and "
                "e2e/scenarios/E12-mixed-load-benchmark.md"),
        }
        result = build_kpi_summary(config, ingress_metrics, control_metrics, resource_metrics, decision)

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
        result["metrics"]["restart_count_total"] = restart_total
        result["metrics"]["oom_count_total"] = int(oom_any)

        with open(os.path.join(args.out, "kpi-summary.json"), "w") as f:
            json.dump(result, f, indent=2)
        with open(os.path.join(args.out, "report.md"), "w") as f:
            f.write(render_report_md(result))
        with open(os.path.join(args.out, "ingest-latency-samples.jsonl"), "w") as f:
            for s in ingest_samples:
                f.write(json.dumps(s) + "\n")
        with open(os.path.join(args.out, "resource-timeseries.jsonl"), "w") as f:
            for t in resource_samples:
                f.write(json.dumps(t) + "\n")

        print(f"[s21] wrote {args.out}/kpi-summary.json and {args.out}/report.md")
        print(json.dumps(result["metrics"], indent=2))
        print(json.dumps(result["split_decision"], indent=2))

        hard_fail = restart_total > 0 or oom_any
        return 1 if hard_fail else 0
    finally:
        for p in seeded:
            try:
                s10.delete_point(args.oxigraph, p)
            except Exception:  # noqa: BLE001
                pass
        try:
            delete_control_point(args.oxigraph, control_point_id)
        except Exception:  # noqa: BLE001
            pass
        print(f"[s21] cleaned up {len(seeded) + 1} seeded points")


def main() -> int:
    args = parse_args(sys.argv[1:])
    return asyncio.run(run(args))


if __name__ == "__main__":
    sys.exit(main())
