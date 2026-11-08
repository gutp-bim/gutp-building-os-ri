#!/usr/bin/env python3
"""S26 — ConnectorWorker ``ingest`` replica scaling (#626 R-1/R-2/R-3).

Drives N gRPC GatewayIngress streams round-robin over the given replicas with the native .NET load
generator (``ingress-loadgen-dotnet``: the Python generator saturates a core at ~2.9k frames/s per
process), then verifies the lake like S22: accepted == lake rows (loss), no (point, time) duplicates.
Per cell it records each replica's accepted rate and CPU/RSS, NATS /varz (CPU, in bytes/s, slow
consumers), JetStream pending, and the load generator's CPU, so the bottleneck can be attributed
(PRD §5.4). Each stream is its own gateway with its own points, as real gateways would be.

The ConnectorWorker replicas (and a separate ``lake`` worker) are started by the caller; this script
only needs their ingress addresses and pids.

Usage:
  python s26_replica_scale.py --out results/S26 --label r1-after-ingest2 \\
      --ingress localhost:15051,localhost:15052 --worker-pid 1234,1235 \\
      --streams 12,24,48 --rate 20000 --duration 60 [--points-per-stream 20] \\
      [--loadgen-cpus 12-15] [--minio-endpoint localhost:9000]
"""
from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import tempfile
import shutil
import time
from datetime import datetime, timezone

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import s10_pointlist_integrity as s10  # noqa: E402
import s22_ingest_rate_sweep as s22  # noqa: E402

LOADGEN_DLL = os.path.join(HERE, "ingress-loadgen-dotnet", "bin", "Release", "net10.0", "IngressLoadGen.dll")


class CompactionAwareLakeCounter(s22.LakeCounter):
    """S22's counter, but it reads an hour partition the way the API does (ParquetLakeReadPlanner):
    when a ``compact-*.parquet`` exists for the hour, only that object counts and the hour's
    ``part-*`` objects are ignored. Runs longer than the compaction settle time (R-4) otherwise
    count the hour twice while compaction's source parts are still being deleted."""

    def _selected(self, keys: list[str]) -> list[str]:
        by_hour: dict[str, list[str]] = {}
        for k in keys:
            by_hour.setdefault(k.rsplit("/", 1)[0], []).append(k)
        out: list[str] = []
        for hour_keys in by_hour.values():
            compact = [k for k in hour_keys if k.rsplit("/", 1)[1].startswith("compact-")]
            out.extend(compact or [k for k in hour_keys if k.rsplit("/", 1)[1].startswith("part-")])
        return out

    def count(self) -> int:
        import duckdb

        keys = self._selected([k for k in self.lc.list_keys(self.endpoint, self.bucket, self.prefix) if k.endswith(".parquet")])
        con = duckdb.connect()
        for k in keys:
            if k in self.rows:
                continue
            try:
                self.rows[k] = con.execute(
                    "SELECT sum(num_rows) FROM parquet_file_metadata(?)", [self._local(k)]).fetchone()[0] or 0
            except Exception:  # noqa: BLE001 — partially written object: retry next poll
                continue
        self._current = set(keys)
        return sum(v for k, v in self.rows.items() if k in self._current)

    def duplicates(self) -> int:
        import duckdb

        files = [self._local(k) for k in self.rows if k in getattr(self, "_current", set(self.rows))]
        if not files:
            return 0
        con = duckdb.connect()
        return con.execute(
            "SELECT coalesce(sum(c - 1), 0) FROM (SELECT count(*) c FROM read_parquet(?, union_by_name=1) "
            "GROUP BY point_id, time HAVING count(*) > 1)", [files]).fetchone()[0]

    def purge(self):
        for k in self.lc.list_keys(self.endpoint, self.bucket, self.prefix):
            self.s3.delete_object(Bucket=self.bucket, Key=k)


LAKE_CONSUMER = "parquetlakewriter"


def lake_pending(nats_monitor: str) -> int:
    """The lake writer's pending count, or -1 when that consumer is not visible on this monitor.

    An empty consumer list must not read as "drained": on a cluster the monitored node may not host
    the consumer (Codex review on #632), and summing nothing gives 0."""
    import kpi_sampler as ks

    _, per = ks.sample_pending(nats_monitor, "VALIDATED")
    hits = [v for k, v in per.items() if k.rsplit("/", 1)[-1] == LAKE_CONSUMER]
    return sum(hits) if hits else -1


def wait_drained(nats_monitor: str, timeout_s: float) -> tuple[int, float]:
    """Block until the lake writer's consumer has no pending messages, so one cell's backlog never
    becomes the next cell's load (a catching-up lake writer reads from the file store and can
    saturate NATS on its own). Returns (pending at exit, seconds waited); -1 = consumer not seen."""
    t0 = time.time()
    pending = -1
    while time.time() - t0 < timeout_s:
        try:
            pending = lake_pending(nats_monitor)
        except Exception:  # noqa: BLE001
            pending = -1
        if pending == 0:
            break
        time.sleep(2)
    return pending, round(time.time() - t0, 1)


def run_cell(args, streams: int, idx: int, run_id: str, pids: list[int]) -> dict:
    targets = [t.strip() for t in args.ingress.split(",") if t.strip()]
    base_gw = f"GW-{run_id}-{idx}"
    prefix = f"{run_id}-{idx}"
    building = f"{run_id}-c{idx}"
    seeded: list[str] = []
    cache = tempfile.mkdtemp(prefix="s26-lake-")
    pb2, pb2g = s10.load_ingress_stubs()
    try:
        # Same naming rule as ingress-loadgen-dotnet: one stream sends as the bare gateway id, N > 1
        # streams as "{gateway}-{i}". Seeding/probing a different name would let the probe pass and
        # every real frame be rejected as gateway_mismatch.
        def gw_of(i: int) -> str:
            return base_gw if streams == 1 else f"{base_gw}-{i}"

        for i in range(streams):
            gw = gw_of(i)
            for j in range(args.points_per_stream):
                p = f"{prefix}-{i}-{j:03d}"
                s10.insert_point(args.oxigraph, p, gw, building)
                seeded.append(p)
        for i in range(streams):
            if not s10.wait_visible(pb2, pb2g, targets[i % len(targets)], gw_of(i), f"{prefix}-{i}-000", timeout_s=120):
                return {"streams": streams, "pass": False, "fail_reasons": [f"stream {i} points not visible"]}
        pre_pending, pre_wait = wait_drained(args.nats_monitor, args.drain_timeout)
        if pre_pending != 0:
            # The previous cell's backlog (or an unreachable monitor) would compete with this load and
            # invalidate the comparison the drain wait exists to protect: refuse the cell.
            return {"streams": streams, "pass": False, "pre_cell_pending": pre_pending, "pre_cell_wait_s": pre_wait,
                    "fail_reasons": [f"lake backlog did not drain before the cell (pending={pre_pending} after {pre_wait}s)"]}
        sampler = s22.Sampler(args.nats_monitor, pids)
        sampler.start()
        cmd = ["dotnet", LOADGEN_DLL, "--target", ",".join(targets), "--gateway", base_gw,
               "--point-prefix", prefix, "--points-per-stream", str(args.points_per_stream),
               "--rate", str(args.rate), "--duration", str(args.duration), "--streams", str(streams)]
        if args.loadgen_cpus:
            cmd = ["taskset", "-c", args.loadgen_cpus] + cmd
        t0 = time.time()
        proc = subprocess.run(cmd, capture_output=True, text=True)
        load_end = time.time()
        lines = [json.loads(l) for l in proc.stdout.splitlines() if l.startswith("{")]
        total = next((l for l in lines if l.get("stream") == "total"), None)
        per_stream = [l for l in lines if l.get("stream") != "total"]
        if total is None:
            sampler.stop()
            return {"streams": streams, "pass": False, "fail_reasons": [f"loadgen rc={proc.returncode}: {proc.stderr[-600:]}"]}
        try:
            end_lag = lake_pending(args.nats_monitor)
        except Exception:  # noqa: BLE001
            end_lag = -1
        if end_lag < 0:
            sampler.stop()
            return {"streams": streams, "pass": False,
                    "fail_reasons": [f"lake consumer '{LAKE_CONSUMER}' not visible on {args.nats_monitor}; "
                                     "point --nats-monitor at the node that hosts it"]}
        lake = CompactionAwareLakeCounter(args.minio_endpoint, args.bucket, building, cache)
        rows, deadline = 0, load_end + args.drain_timeout
        while time.time() < deadline:
            rows = lake.count()
            if rows >= total["accepted"]:
                break
            time.sleep(5)
        drain_s = round(time.time() - load_end, 1)
        sampler.stop()
        dup = lake.duplicates() if rows else 0
        step = s22.assess_step(args.rate, args.duration, total["accepted"], total["elapsed_s"], rows, dup, end_lag,
                               args.lag_seconds)
        win = [s for s in sampler.samples if t0 + 2 <= s["t"] <= load_end]
        per_replica: dict[str, dict] = {}
        for r in per_stream:
            d = per_replica.setdefault(r["target"], {"streams": 0, "accepted": 0, "sent": 0})
            d["streams"] += 1
            d["accepted"] += r["accepted"]
            d["sent"] += r["sent"]
        for d in per_replica.values():
            d["achieved_rate"] = round(d["accepted"] / total["elapsed_s"], 1)
        pct = s22.percentile
        step.update({
            "streams": streams, "replicas": len(targets), "sent": total["sent"], "refused": total["sent"] - total["accepted"],
            "drain_s": drain_s, "per_replica": per_replica,
            "pre_cell_pending": pre_pending, "pre_cell_wait_s": pre_wait,
            "peak_lag": max((s["pending"] for s in sampler.samples if s["pending"] >= 0), default=None),
            "worker_cpu_pct_p50_by_replica": [
                pct([s["worker_cpu_pct_by_replica"][i] for s in win
                     if "worker_cpu_pct_by_replica" in s and s["worker_cpu_pct_by_replica"][i] is not None], .5)
                for i in range(len(pids))],
            "worker_rss_mb_max_by_replica": [
                pct([s["worker_rss_mb_by_replica"][i] for s in win
                     if "worker_rss_mb_by_replica" in s and s["worker_rss_mb_by_replica"][i] is not None], 1.0)
                for i in range(len(pids))],
            "nats_cpu_pct_p50": pct([s["nats_cpu_pct"] for s in win if s.get("nats_cpu_pct") is not None], .5),
            "nats_cpu_pct_max": pct([s["nats_cpu_pct"] for s in win if s.get("nats_cpu_pct") is not None], 1.0),
            "nats_in_bytes_per_s_p50": pct([s["nats_in_bytes_per_s"] for s in win if "nats_in_bytes_per_s" in s], .5),
            "nats_slow_consumers_max": pct([s["nats_slow_consumers"] for s in win if s.get("nats_slow_consumers") is not None], 1.0),
            "host_cpu_pct_p50": pct([s["host_cpu_pct"] for s in win if "host_cpu_pct" in s], .5),
            "loadgen_cpu_pct_p50": pct([s["loadgen_cpu_pct"] for s in win if "loadgen_cpu_pct" in s], .5),
            "samples": sampler.samples,
        })
        if not args.keep_lake:
            lake.purge()
        return step
    finally:
        for p in seeded:
            try:
                s10.delete_point(args.oxigraph, p)
            except Exception:  # noqa: BLE001
                pass
        shutil.rmtree(cache, ignore_errors=True)


def render_md(run_id: str, meta: dict, steps: list[dict]) -> str:
    lines = [f"# S26 ingest replica scaling — {run_id}" + (f" ({meta.get('label')})" if meta.get("label") else ""), "",
             f"commit {meta.get('commit')} · host {meta.get('host')} · ingress {meta['args']['ingress']} · rate {meta['args']['rates']}/s", "",
             "| streams | replicas | achieved/s | ratio | loss | dup | per replica accepted/s | worker CPU% p50 (each) | NATS CPU% p50/max "
             "| loadgen CPU% | peak lag | result |",
             "|--:|--:|--:|--:|--:|--:|---|---|--:|--:|--:|---|"]
    for s in steps:
        if "achieved_rate" not in s:
            lines.append(f"| {s.get('streams')} | - | - | - | - | - | - | - | - | - | - | FAIL: {'; '.join(s['fail_reasons'])} |")
            continue
        per = " / ".join(f"{d['achieved_rate']:.0f}" for d in s["per_replica"].values())
        cpu = " / ".join(f"{c:.0f}" if c is not None else "-" for c in s["worker_cpu_pct_p50_by_replica"])
        lines.append(
            f"| {s['streams']} | {s['replicas']} | {s['achieved_rate']:.0f} | {s['achieved_ratio']:.1%} | {s['loss_rate']} "
            f"| {s['duplicate_rate']} | {per} | {cpu} | {s['nats_cpu_pct_p50']}/{s['nats_cpu_pct_max']} "
            f"| {s['loadgen_cpu_pct_p50']} | {s['peak_lag']} | {'PASS' if s['pass'] else 'FAIL: ' + '; '.join(s['fail_reasons'])} |")
    return "\n".join(lines) + "\n"


def main() -> int:
    ap = argparse.ArgumentParser(description="S26 ingest replica scaling")
    ap.add_argument("--out", default="results/S26")
    ap.add_argument("--label", default=None)
    ap.add_argument("--ingress", required=True, help="comma-separated replica addresses; streams go round-robin")
    ap.add_argument("--worker-pid", default=None, help="comma-separated pids of the ingest replicas, same order")
    ap.add_argument("--streams", default="12,24,48")
    ap.add_argument("--rate", type=int, default=20000, help="total target frames/s, split evenly over streams")
    ap.add_argument("--duration", type=int, default=60)
    ap.add_argument("--points-per-stream", type=int, default=20)
    ap.add_argument("--lag-seconds", type=float, default=5.0)
    ap.add_argument("--drain-timeout", type=int, default=240)
    ap.add_argument("--keep-lake", action="store_true", help="leave the cell's lake objects in place")
    ap.add_argument("--loadgen-cpus", default=None, help="taskset CPU list for the load generator, e.g. 12-15")
    ap.add_argument("--oxigraph", default=os.environ.get("OXIGRAPH_URL", "http://localhost:7878"))
    ap.add_argument("--minio-endpoint", default=os.environ.get("MINIO_ENDPOINT_HOST", "localhost:9000"))
    ap.add_argument("--bucket", default=os.environ.get("LAKE_BUCKET", "cold"))
    ap.add_argument("--nats-monitor", default=os.environ.get("NATS_MONITOR_URL", "http://localhost:8222"))
    args = ap.parse_args()
    if not os.path.exists(LOADGEN_DLL):
        raise SystemExit(f"build the load generator first: dotnet build -c Release {os.path.dirname(LOADGEN_DLL)}")
    # run_metadata reads these off args (shared with S22)
    args.rates, args.points_per_gateway, args.procs, args.max_streams = str(args.rate), args.points_per_stream, 1, None
    run_id = "s26" + datetime.now(timezone.utc).strftime("%m%d%H%M%S")
    out = os.path.join(args.out, run_id)
    os.makedirs(out, exist_ok=True)
    pids = s22.find_worker_pids(args.worker_pid)
    meta = s22.run_metadata(args, pids)
    meta["args"]["streams"] = args.streams
    print(f"[s26] run={run_id} label={args.label} ingress={args.ingress} pids={pids} rate={args.rate}", flush=True)
    steps = []
    for idx, n in enumerate(int(x) for x in args.streams.split(",") if x.strip()):
        print(f"[s26] cell {idx + 1}: {n} streams over {len(args.ingress.split(','))} replica(s) at {args.rate}/s ...", flush=True)
        try:
            step = run_cell(args, n, idx, run_id, pids)
        except Exception as ex:  # noqa: BLE001 — one broken cell (e.g. a probe timing out) must not end the series
            step = {"streams": n, "pass": False, "fail_reasons": [f"cell aborted: {type(ex).__name__}: {str(ex)[:200]}"]}
        steps.append(step)
        print(f"[s26]   -> {'PASS' if step['pass'] else 'FAIL'} achieved={step.get('achieved_rate')} "
              f"rows={step.get('lake_rows')}/{step.get('accepted')} per_replica="
              f"{ {k: v['achieved_rate'] for k, v in (step.get('per_replica') or {}).items()} } "
              f"nats_cpu={step.get('nats_cpu_pct_p50')} {step.get('fail_reasons') or ''}", flush=True)
        with open(os.path.join(out, "scale.json"), "w") as f:
            json.dump({"run_id": run_id, "meta": meta, "steps": steps}, f, indent=2)
    md = render_md(run_id, meta, [{k: v for k, v in s.items() if k != "samples"} for s in steps])
    with open(os.path.join(out, "report.md"), "w") as f:
        f.write(md)
    print(md)
    print(f"[s26] wrote {out}")
    return 0 if steps and all(st.get("pass") for st in steps) else 1


if __name__ == "__main__":
    sys.exit(main())
