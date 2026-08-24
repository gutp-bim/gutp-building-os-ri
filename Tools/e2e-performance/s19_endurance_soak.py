#!/usr/bin/env python3
"""E10 — 長時間ソーク試験 (endurance soak, #297 follow-up / e2e 統合版).

gRPC GatewayIngress で現実的な点数・周期の持続負荷を数時間かけつつ、以下を同時サンプリングする:

  1. コンテナ RSS（`docker stats`）— Connector Worker / NATS の定常性を #297 と同じ方法論
     （開始1時間平均 vs 終了1時間平均、後半のみの回帰スロープ）で観測する。
  2. NATS validated stream の consumer pending（`kpi_sampler.py` のロジックを再利用）。
  3. 各サービスの health probe（NATS/OxiGraph/MinIO/ConnectorWorker/API を直接叩く。#297 と同じ
     く単純な成功率を記録）。
  4. コンテナ再起動回数・OOM 有無（`docker inspect`）。

送信は 1 本の gRPC ストリームを --chunk-seconds 毎に張り直す（#297 は単一 24h ストリームだったが、
本スクリプトは途中で connector-worker が再起動しても計測を継続できるようにするため）。終了時に
`quality_checker.py`（parquet mode）で送信数と lake 永続化数を突き合わせ、loss/duplicate を出す。

これは #297 が要求する ≥72h・確定閾値版の代替ではなく、e2e/ 評価軸に組み込んだ短時間（既定 4-6h）の
反復版。メモリ増加量はまだ安全域が確定していないため `report`（情報値）として出力し、gate は
再起動ゼロ・OOMゼロ・データ整合・health probe 成功率のみを判定する（`e2e/kpi-thresholds.yaml`
E10_endurance_soak）。

Usage:
  python s19_endurance_soak.py --out results/E10 --duration-hours 4 --rate 6 --points 1865
      [--chunk-seconds 300] [--sample-interval 60]
      [--ingress localhost:5051] [--oxigraph http://localhost:7878]
      [--minio-endpoint localhost:9000] [--flush-wait 90]
      [--containers building-os.connector-worker,building-os.nats,building-os.oxigraph,building-os.api,building-os.minio]
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
from datetime import datetime, timezone

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import s10_pointlist_integrity as s10  # noqa: E402
import kpi_sampler as kpis  # noqa: E402

import requests  # noqa: E402

DEFAULT_CONTAINERS = [
    "building-os.connector-worker",
    "building-os.nats",
    "building-os.oxigraph",
    "building-os.minio",
]

# API server is not on the E10 ingest path (gRPC GatewayIngress -> connector-worker -> NATS ->
# Parquet writer bypasses it entirely), so it is intentionally not started/probed for this axis.
DEFAULT_HEALTH_PROBES = {
    "nats": "http://localhost:8222/healthz",
    "oxigraph": "http://localhost:7878/",
    "minio": "http://localhost:9000/minio/health/live",
    "connector-worker": "http://localhost:8081/health/ready",
}

_MEM_UNIT = {"B": 1 / (1024 * 1024), "KIB": 1 / 1024, "KB": 1 / 1024, "MIB": 1.0, "MB": 1.0,
             "GIB": 1024.0, "GB": 1024.0}


def _parse_mem_to_mib(text: str) -> float | None:
    """'123.4MiB' / '1.943GiB' -> MiB (float). None if unparsable."""
    m = re.match(r"([\d.]+)\s*([A-Za-z]+)", text.strip())
    if not m:
        return None
    val, unit = float(m.group(1)), m.group(2).upper()
    factor = _MEM_UNIT.get(unit)
    return val * factor if factor is not None else None


def docker_stats(containers: list[str]) -> dict[str, float]:
    """One `docker stats --no-stream` call -> {container: rss_mib} for the ones we care about."""
    try:
        out = subprocess.run(
            ["docker", "stats", "--no-stream", "--format", "{{.Name}}\t{{.MemUsage}}"],
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
        mem_used = parts[1].split("/")[0].strip()
        mib = _parse_mem_to_mib(mem_used)
        if mib is not None:
            result[parts[0]] = round(mib, 2)
    return result


def docker_restart_state(containers: list[str]) -> dict[str, dict]:
    """One `docker inspect` call -> {container: {restart_count, oom_killed}}."""
    try:
        out = subprocess.run(
            ["docker", "inspect", "--format",
             "{{.Name}}|{{.RestartCount}}|{{.State.OOMKilled}}", *containers],
            capture_output=True, text=True, timeout=15, check=False,
        ).stdout
    except (subprocess.SubprocessError, OSError):
        return {}
    result: dict[str, dict] = {}
    for line in out.splitlines():
        bits = line.split("|")
        if len(bits) != 3:
            continue
        name = bits[0].lstrip("/")
        result[name] = {"restart_count": int(bits[1]), "oom_killed": bits[2] == "true"}
    return result


def probe_health(probes: dict[str, str]) -> dict[str, bool]:
    out: dict[str, bool] = {}
    for name, url in probes.items():
        try:
            r = requests.get(url, timeout=5)
            out[name] = r.status_code < 500
        except requests.RequestException:
            out[name] = False
    return out


async def stream_chunk(pb2, pb2g, target: str, gw: str, points: list[str], rate: float,
                        seconds: int) -> tuple[int, int, str | None]:
    """Stream ~rate/s for `seconds` on a fresh gRPC stream. Returns (sent, accepted, error)."""
    import grpc  # type: ignore

    interval = 1.0 / rate if rate > 0 else 0.0
    total = max(1, round(rate * seconds))
    sent = 0

    async def gen():
        nonlocal sent
        for i in range(total):
            p = points[i % len(points)]
            yield pb2.TelemetryFrame(gateway_id=gw, point_id=p, value_num=20.0 + (i % 100) / 10.0,
                                     timestamp=datetime.now(timezone.utc).isoformat())
            sent += 1
            if interval:
                await asyncio.sleep(interval)

    try:
        async with grpc.aio.insecure_channel(target) as ch:
            ack = await asyncio.wait_for(pb2g.GatewayIngressStub(ch).StreamTelemetry(gen()),
                                          timeout=seconds + 30)
        return sent, int(ack.accepted), None
    except Exception as e:  # noqa: BLE001 — chunk failure must not kill the soak
        return sent, 0, f"{type(e).__name__}: {e}"


async def ingest_loop(pb2, pb2g, args, gw: str, points: list[str], out_dir: str,
                       stop_at: float) -> dict:
    path = os.path.join(out_dir, "ingest-timeseries.jsonl")
    total_sent = total_accepted = 0
    errors = 0
    with open(path, "a") as fh:
        while time.monotonic() < stop_at:
            remaining = stop_at - time.monotonic()
            chunk_s = int(min(args.chunk_seconds, max(1, remaining)))
            sent, accepted, err = await stream_chunk(pb2, pb2g, args.ingress, gw, points,
                                                       args.rate, chunk_s)
            total_sent += sent
            total_accepted += accepted
            if err:
                errors += 1
            rec = {"ts": datetime.now(timezone.utc).isoformat(), "chunk_seconds": chunk_s,
                   "sent": sent, "accepted": accepted, "error": err}
            fh.write(json.dumps(rec) + "\n")
            fh.flush()
            print(f"[s19][ingest] sent={sent} accepted={accepted}"
                  f"{' error=' + err if err else ''}")
            if err:
                await asyncio.sleep(5)  # backoff before next chunk on failure
    return {"sent": total_sent, "accepted": total_accepted, "chunk_errors": errors}


def resource_sample_tick(containers: list[str], probes: dict[str, str]) -> dict:
    mem = docker_stats(containers)
    restarts = docker_restart_state(containers)
    health = probe_health(probes)
    try:
        pending_total, pending_per = kpis.sample_pending(
            os.environ.get("NATS_MONITOR_URL", "http://localhost:8222"), "VALIDATED")
    except requests.RequestException:
        pending_total, pending_per = -1, {}
    return {"mem_mib": mem, "restarts": restarts, "health": health,
            "consumer_pending_total": pending_total, "consumer_pending": pending_per}


def resource_role_main(args) -> int:
    """Standalone entry point (spawned as a child process — see run()). Runs the sampling loop
    synchronously in its own process/interpreter so its `docker`/`curl`-equivalent subprocess calls
    never fork() inside a process that also holds a live grpc.aio channel (grpc + fork is unsafe:
    https://github.com/grpc/grpc/blob/master/doc/fork_support.md). Writes resource-timeseries.jsonl
    only; the parent re-reads it and computes the summary after both roles finish."""
    containers = args.containers.split(",")
    path = os.path.join(args.out, "resource-timeseries.jsonl")
    duration_s = args.duration_hours * 3600
    stop_at = time.monotonic() + duration_s
    start = time.monotonic()
    with open(path, "a") as fh:
        while True:
            elapsed = round(time.monotonic() - start, 1)
            tick = resource_sample_tick(containers, DEFAULT_HEALTH_PROBES)
            rec = {"ts": datetime.now(timezone.utc).isoformat(), "elapsed_s": elapsed, **tick}
            fh.write(json.dumps(rec) + "\n")
            fh.flush()
            now = time.monotonic()
            if now >= stop_at:
                break
            sleep_s = min(args.sample_interval, stop_at - now)
            time.sleep(max(1.0, sleep_s))
    return 0


def read_resource_samples(out_dir: str) -> list[dict]:
    path = os.path.join(out_dir, "resource-timeseries.jsonl")
    samples: list[dict] = []
    if not os.path.isfile(path):
        return samples
    with open(path) as fh:
        for line in fh:
            line = line.strip()
            if line:
                samples.append(json.loads(line))
    return samples


def _container_series(samples: list[dict], container: str) -> tuple[list[float], list[float]]:
    xs, ys = [], []
    for s in samples:
        v = s["mem_mib"].get(container)
        if v is not None:
            xs.append(s["elapsed_s"] / 3600.0)
            ys.append(v)
    return xs, ys


def summarize_resources(samples: list[dict], containers: list[str],
                         baseline_restarts: dict[str, dict] | None = None) -> dict:
    """baseline_restarts ({container: {restart_count, oom_killed}}) should be captured *before* the
    soak's load starts (docker's RestartCount is cumulative since container creation, so a stack
    that was merely brought up moments earlier can already show a nonzero count unrelated to this
    run) — restart_count_total/oom_count_total below are deltas against that baseline, not raw
    Docker counters. Falls back to the first in-run sample when no baseline is supplied."""
    metrics: dict = {}
    restart_total = 0
    oom_any = False
    baseline_restarts = baseline_restarts or {}
    for c in containers:
        xs, ys = _container_series(samples, c)
        key = c.replace("building-os.", "").replace("-", "_")
        if ys:
            n = len(ys)
            first_hour = [y for x, y in zip(xs, ys) if x <= 1.0] or ys[: max(1, n // 10)]
            last_hour_cut = xs[-1] - 1.0 if xs else 0
            last_hour = [y for x, y in zip(xs, ys) if x >= last_hour_cut] or ys[-max(1, n // 10):]
            half = ys[n // 2:]
            half_x = xs[n // 2:]
            slope_per_hour = kpis._slope(half_x, half)
            metrics[f"{key}_rss_start_mib"] = round(ys[0], 1)
            metrics[f"{key}_rss_end_mib"] = round(ys[-1], 1)
            metrics[f"{key}_rss_max_mib"] = round(max(ys), 1)
            metrics[f"{key}_rss_first_hour_avg_mib"] = round(sum(first_hour) / len(first_hour), 1)
            metrics[f"{key}_rss_last_hour_avg_mib"] = round(sum(last_hour) / len(last_hour), 1)
            metrics[f"{key}_rss_growth_mib_per_hour"] = round(slope_per_hour, 2)
        # restart/OOM delta: last sample that reported this container, minus the pre-soak baseline
        base = baseline_restarts.get(c)
        if base is None:
            for s in samples:
                r = s["restarts"].get(c)
                if r:
                    base = r
                    break
        for s in reversed(samples):
            r = s["restarts"].get(c)
            if r:
                base_count = base["restart_count"] if base else 0
                base_oom = base["oom_killed"] if base else False
                restart_total += max(0, r["restart_count"] - base_count)
                oom_any = oom_any or (r["oom_killed"] and not base_oom)
                break
    health_names = {k for s in samples for k in s["health"]}
    total_probes = ok_probes = 0
    for s in samples:
        for name in health_names:
            if name in s["health"]:
                total_probes += 1
                ok_probes += 1 if s["health"][name] else 0
    metrics["health_probe_success_rate"] = round(ok_probes / total_probes, 4) if total_probes else None
    metrics["restart_count_total"] = restart_total
    metrics["oom_count_total"] = int(oom_any)

    pend = [s["consumer_pending_total"] for s in samples if s["consumer_pending_total"] >= 0]
    if pend:
        pend_x = [s["elapsed_s"] for s in samples if s["consumer_pending_total"] >= 0]
        half = pend[len(pend) // 2:]
        half_x = pend_x[len(pend_x) // 2:]
        slope = kpis._slope(half_x, half)
        metrics["consumer_pending_max"] = max(pend)
        metrics["consumer_pending_last"] = pend[-1]
        metrics["consumer_pending_slope_per_sec"] = round(slope, 4)
        metrics["pending_stable"] = int(slope <= 1.0)
    else:
        # No valid NATS pending sample the whole run (monitoring unreachable throughout) — emit an
        # explicit failing 0 rather than leaving pending_stable absent, so gate.py FAILs the KPI
        # instead of SKIPping it (missing data must not look like a passing run).
        metrics["pending_stable"] = 0
    metrics["resource_samples"] = len(samples)
    return metrics


def run_quality_checker(run_id: str, building: str, expected: int, minio_endpoint: str) -> dict | None:
    perf = os.path.dirname(os.path.abspath(__file__))
    py = os.path.join(perf, ".venv", "bin", "python")
    py = py if os.path.exists(py) else sys.executable
    cmd = [py, os.path.join(perf, "quality_checker.py"),
           "--run-id", run_id, "--building", building, "--expected", str(expected),
           "--mode", "parquet", "--minio-endpoint", minio_endpoint]
    subprocess.run(cmd, check=False, capture_output=True, text=True, timeout=300)
    result_path = os.path.join(perf, "results", run_id, "quality-check-result.json")
    if os.path.isfile(result_path):
        with open(result_path) as f:
            return json.load(f)
    return None


async def run(args) -> int:
    pb2, pb2g = s10.load_ingress_stubs()
    tag = datetime.now(timezone.utc).strftime("%Y%m%d%H%M%S")
    run_id = f"soak-{tag}"
    gw, building = f"GW-SOAK-{tag}", run_id
    points = [f"soak-{tag}-{i:05d}" for i in range(args.points)]
    containers = args.containers.split(",")

    os.makedirs(args.out, exist_ok=True)
    seeded: list[str] = []
    try:
        print(f"[s19] seeding {len(points)} points (gw={gw}, building={building})...")
        for p in points:
            s10.insert_point(args.oxigraph, p, gw, building)
            seeded.append(p)
        if not s10.wait_visible(pb2, pb2g, args.ingress, gw, points[0]):
            print("seeded points not visible — aborting", file=sys.stderr)
            return 2

        baseline_restarts = docker_restart_state(containers)
        stop_at = time.monotonic() + args.duration_hours * 3600
        print(f"[s19] soak start: {args.duration_hours}h @ ~{args.rate}/s "
              f"({args.points} points), sampling every {args.sample_interval}s")

        # Resource sampling runs in its own OS process (re-invoking this file with --role resource)
        # rather than a thread in this process: its `docker` subprocess calls must not fork() while
        # this process also holds a live grpc.aio channel (fork-after-grpc-threads is unsafe — see
        # resource_role_main's docstring). It shares the same --out dir and writes
        # resource-timeseries.jsonl, which we read back after both finish.
        resource_proc = subprocess.Popen([
            sys.executable, os.path.abspath(__file__), "--role", "resource",
            "--out", args.out, "--duration-hours", str(args.duration_hours),
            "--sample-interval", str(args.sample_interval), "--containers", args.containers,
        ])

        ingest_result = await ingest_loop(pb2, pb2g, args, gw, points, args.out, stop_at)
        loop = asyncio.get_event_loop()
        await loop.run_in_executor(None, resource_proc.wait)
        resource_metrics = summarize_resources(read_resource_samples(args.out), containers,
                                                baseline_restarts)
        resource_metrics["resource_sampler_exit_code"] = resource_proc.returncode
        if resource_proc.returncode != 0:
            # Child crashed (or never wrote a full run's worth of samples) — an empty/partial
            # timeseries would otherwise summarize as restart_count_total=0 / health checks absent,
            # which reads as a clean pass. Force the two data-availability-dependent KPIs to an
            # explicit failing value rather than let missing data look like success.
            print(f"[s19] WARNING: resource sampler exited {resource_proc.returncode} — "
                  f"forcing health/pending KPIs to fail (data for this run window is unreliable)",
                  file=sys.stderr)
            resource_metrics["health_probe_success_rate"] = 0.0
            resource_metrics["pending_stable"] = 0

        print(f"[s19] ingest done: sent={ingest_result['sent']} "
              f"accepted={ingest_result['accepted']} chunk_errors={ingest_result['chunk_errors']}; "
              f"waiting {args.flush_wait}s for flush...")
        await asyncio.sleep(args.flush_wait)

        # Reconcile against `sent` (frames the client actually generated), not `accepted`: a chunk
        # whose gRPC Ack timed out client-side still reports accepted=0 even though the server had
        # already processed most/all of its frames (see resource_role_main's docstring on why
        # resource sampling is isolated from this — the Ack wait, not the transfer, is what times
        # out under sustained chunked load). Using `accepted` here would understate `expected` and
        # let loss_rate mask real gaps once db_count exceeds it (evaluate() clips loss at 0).
        qc = run_quality_checker(run_id, building, ingest_result["sent"], args.minio_endpoint)
        if qc is None:
            # Match s15_ingest_throughput.py's behavior: a missing quality-check-result.json means
            # we cannot vouch for data integrity at all. Leaving loss/dup/rows as None here would
            # make gate.py SKIP those KPIs (absent metric), not FAIL — letting a broken
            # reconciliation step look like a passing run. Report an explicit worst case instead.
            print("[s19] quality_checker produced no result — treating as 100% loss", file=sys.stderr)
            loss, dup, invalid, rows = 1.0, 0.0, 0, 0
        else:
            loss = float(qc.get("loss_rate", 0.0))
            dup = float(qc.get("duplicate_rate", 0.0))
            invalid = int(qc.get("schema_invalid_count", 0))
            rows = int(qc.get("db_row_count", 0))

        metrics = {
            **resource_metrics,
            "sent_total": ingest_result["sent"],
            "accepted_total": ingest_result["accepted"],
            "chunk_errors": ingest_result["chunk_errors"],
            "lake_rows": rows,
            "data_loss_ratio": round(loss, 6),
            "duplicate_rate": round(dup, 6),
            "schema_invalid_count": invalid,
        }
        result = {
            "axis": "E10_endurance_soak",
            "generated_at": datetime.now(timezone.utc).isoformat(),
            "config": {"duration_hours": args.duration_hours, "rate": args.rate,
                       "points": args.points, "chunk_seconds": args.chunk_seconds,
                       "sample_interval_s": args.sample_interval, "containers": containers,
                       "run_id": run_id},
            "metrics": metrics,
        }
        out_path = os.path.join(args.out, "E10-soak.json")
        with open(out_path, "w") as f:
            json.dump(result, f, indent=2)
        print(f"[s19] wrote {out_path}")
        print(json.dumps(metrics, indent=2))
        hard_fail = (
            metrics.get("restart_count_total", 0) > 0
            or metrics.get("oom_count_total", 0) > 0
            or loss > 0.01
            or resource_proc.returncode != 0
        )
        return 1 if hard_fail else 0
    finally:
        for p in seeded:
            try:
                s10.delete_point(args.oxigraph, p)
            except Exception:  # noqa: BLE001
                pass
        print(f"[s19] cleaned up {len(seeded)} seeded points")


def main() -> int:
    ap = argparse.ArgumentParser(description="E10 endurance soak (#297 follow-up)")
    ap.add_argument("--role", choices=["full", "resource"], default="full",
                     help="internal: 'resource' is the child-process entry point run() spawns for "
                          "sampling; end users always want the default 'full'")
    ap.add_argument("--out", default="results/E10")
    ap.add_argument("--duration-hours", type=float, default=4.0)
    ap.add_argument("--rate", type=float, default=6.2167,
                     help="target frames/sec (aggregate; default matches #297's 1865pt/300s THX cadence)")
    ap.add_argument("--points", type=int, default=1865, help="distinct points (default: #297 THX scale)")
    ap.add_argument("--chunk-seconds", type=int, default=300, help="gRPC stream re-open cadence")
    ap.add_argument("--sample-interval", type=int, default=60, help="resource sampling interval (s)")
    ap.add_argument("--ingress", default=os.environ.get("INGRESS_TARGET", "localhost:5051"))
    ap.add_argument("--oxigraph", default=os.environ.get("OXIGRAPH_URL", "http://localhost:7878"))
    ap.add_argument("--minio-endpoint", default=os.environ.get("MINIO_ENDPOINT_HOST", "localhost:9000"))
    ap.add_argument("--flush-wait", type=int, default=90)
    ap.add_argument("--containers", default=",".join(DEFAULT_CONTAINERS))
    args = ap.parse_args()
    if args.role == "resource":
        os.makedirs(args.out, exist_ok=True)
        return resource_role_main(args)
    return asyncio.run(run(args))


if __name__ == "__main__":
    sys.exit(main())
