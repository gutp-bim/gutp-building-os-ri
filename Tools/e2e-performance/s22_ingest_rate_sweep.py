#!/usr/bin/env python3
"""S22 — gRPC GatewayIngress ingest rate sweep (throughput ceiling / knee finder).

Existing evidence (E1/S15/S17) proves correctness at ~200 frames/s or one frame per Point; none of it
finds where the pipeline saturates. This raises the sustained frame rate step by step on the canonical
path (gRPC GatewayIngress -> NATS building-os.validated.telemetry -> ParquetLakeWriter -> object store)
and records, per step: achieved/accepted rate, JetStream consumer lag, drain time until the lake holds
every accepted row, loss/duplicates, and CPU/RSS of the ConnectorWorker.

Why not s15: it drives one stream paced with ``asyncio.sleep(1/rate)`` per frame, which cannot exceed
roughly 1k frames/s. Here each gateway streams from its own process, pacing is per 20 ms batch (no
per-frame sleep), and the lake is counted by listing/downloading parquet objects with boto3 and reading
the footers with a *local* DuckDB (no httpfs extension download, so it works behind an egress proxy).

Note: ``drain_s`` is quantised by PARQUET_FLUSH_INTERVAL (minutes) — rows become visible only after a
flush — so it measures "lag behind the writer", not per-frame latency. Backlog growth is read from the
JetStream consumer pending samples instead.

Pass/saturation per step (``assess_step``): PASS needs achieved >= 95% of target, accepted == lake rows
(loss <= 1%), duplicates <= 0.5% and the end-of-load JetStream lag below ``lag_seconds`` worth of
frames. The first non-PASS step is the knee; the sweep stops there unless --continue-on-failure.

Usage:
  python s22_ingest_rate_sweep.py --out results/S22 --rates 500,1000,2000,5000,10000,20000 \
      [--duration 60] [--points-per-gateway 100] [--ingress localhost:5051] [--oxigraph http://localhost:7878] \
      [--minio-endpoint localhost:9000] [--nats-monitor http://localhost:8222] [--worker-pid N[,N2,…]] [--label tag]

Several ConnectorWorker replicas (#626): list them in ``--ingress host:port,host:port`` (streams go
round-robin) and ``--worker-pid pid,pid`` (per-replica CPU/RSS); ``per_replica`` in sweep.json then
carries each replica's accepted rate, and every sample also records NATS /varz (cpu, in bytes/s, slow
consumers) and the load generator's own CPU so the bottleneck can be attributed (PRD §5.4).
"""

from __future__ import annotations

import argparse
import asyncio
import concurrent.futures as cf
import json
import math
import multiprocessing as mp
import os
import platform
import shutil
import subprocess
import sys
import tempfile
import threading
import time
from datetime import datetime, timedelta, timezone

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))

TICK_S = 0.02
ACHIEVED_MIN = 0.95
LOSS_MAX = 0.01
DUP_MAX = 0.005


# ── pure helpers (unit-tested) ────────────────────────────────────────────────────────────────────
def parse_rates(text: str) -> list[int]:
    rates = [int(x) for x in text.split(",") if x.strip()]
    if not rates or any(r <= 0 for r in rates):
        raise ValueError("rates must be positive integers")
    return rates


def plan_streams(rate: int, max_streams: int, per_stream_cap: int = 5000) -> int:
    """One stream per ``per_stream_cap`` frames/s, bounded by the load generator's CPU budget."""
    return max(1, min(max_streams, math.ceil(rate / per_stream_cap)))


def split_rate(rate: int, streams: int) -> list[int]:
    base, rem = divmod(rate, streams)
    return [base + (1 if i < rem else 0) for i in range(streams)]


def assess_step(target: int, duration: int, accepted: int, elapsed: float, lake_rows: int,
                duplicates: int, end_lag: int, lag_seconds: float = 5.0) -> dict:
    """Judge one step. ``end_lag`` is the JetStream consumer pending at the moment the load ended."""
    achieved = accepted / elapsed if elapsed > 0 else 0.0
    achieved_ratio = achieved / target if target else 0.0
    # lake_rows can exceed accepted by the few readiness-probe frames the harness sends while waiting
    # for the seeded points to become visible; that is not loss, and real extras show up as duplicates.
    loss = max(0.0, 1.0 - lake_rows / accepted) if accepted else 1.0
    dup = duplicates / lake_rows if lake_rows else 0.0
    lag_limit = int(target * lag_seconds)
    reasons = []
    if achieved_ratio < ACHIEVED_MIN:
        reasons.append(f"achieved {achieved_ratio:.1%} < {ACHIEVED_MIN:.0%} of target")
    if loss > LOSS_MAX:
        reasons.append(f"loss {loss:.2%} > {LOSS_MAX:.0%}")
    if dup > DUP_MAX:
        reasons.append(f"duplicates {dup:.2%} > {DUP_MAX:.1%}")
    if end_lag > lag_limit:
        reasons.append(f"end lag {end_lag} > {lag_limit} (={lag_seconds:g}s of load)")
    return {"target_rate": target, "duration_s": duration, "accepted": accepted,
            "achieved_rate": round(achieved, 1), "achieved_ratio": round(achieved_ratio, 4),
            "lake_rows": lake_rows, "loss_rate": round(loss, 6), "duplicates": duplicates,
            "duplicate_rate": round(dup, 6), "end_lag": end_lag, "pass": not reasons,
            "fail_reasons": reasons}


def percentile(values: list[float], p: float) -> float | None:
    if not values:
        return None
    s = sorted(values)
    return s[min(len(s) - 1, int(round(p * (len(s) - 1))))]


def parse_proc_stat(text: str) -> int:
    """utime+stime (clock ticks) from /proc/<pid>/stat; robust to spaces in the comm field."""
    rest = text[text.rindex(")") + 2:].split()
    return int(rest[11]) + int(rest[12])


# ── load generation (runs in child processes) ─────────────────────────────────────────────────────
def load_worker(specs: list[tuple[str, str, list[str], int]], duration: int, start_at: float) -> list[dict]:
    """Drive several client-streams concurrently from ONE process (one asyncio loop).

    ``specs`` = [(target, gateway_id, points, rate)]. Packing many streams into few processes keeps the
    load generator's CPU out of the measurement when the stream count is high.
    """
    import grpc  # type: ignore
    import s10_pointlist_integrity as s10

    pb2, pb2g = s10.load_ingress_stubs()

    async def one(target: str, gw: str, points: list[str], rate: int) -> dict:
        sent = 0

        async def gen():
            nonlocal sent
            t0 = start_at
            total = rate * duration
            npts = len(points)
            while sent < total:
                now = time.time()
                if now >= t0 + duration:
                    break
                due = min(total, int((now - t0) * rate)) if now >= t0 else 0
                base = datetime.now(timezone.utc)
                if due <= sent:
                    await asyncio.sleep(TICK_S)
                    continue
                for k in range(due - sent):
                    # unique (point, time) per frame: 1 us apart inside a batch
                    ts = (base + timedelta(microseconds=k)).isoformat()
                    yield pb2.TelemetryFrame(gateway_id=gw, point_id=points[sent % npts],
                                             value_num=20.0 + (sent % 100) / 10.0, timestamp=ts)
                    sent += 1
                await asyncio.sleep(0)  # let gRPC flush; backpressure blocks the yield above

        delay = start_at - time.time()
        if delay > 0:
            await asyncio.sleep(delay)
        t = time.perf_counter()
        async with grpc.aio.insecure_channel(target, options=[("grpc.max_send_message_length", -1)]) as ch:
            ack = await pb2g.GatewayIngressStub(ch).StreamTelemetry(gen())
        return {"gateway": gw, "target": target, "sent": sent, "accepted": int(ack.accepted),
                "elapsed_s": time.perf_counter() - t}

    async def run() -> list[dict]:
        return list(await asyncio.gather(*[one(*sp) for sp in specs]))

    return asyncio.run(run())


# ── sampling ──────────────────────────────────────────────────────────────────────────────────────
def jsz_pending(nats_monitor: str, stream_filter: str = "VALIDATED") -> tuple[int, int]:
    import requests
    import kpi_sampler as ks

    total, _ = ks.sample_pending(nats_monitor, stream_filter)
    st = ks.sample_stream_state(nats_monitor, stream_filter)
    return total, st["messages"]


def varz(nats_monitor: str) -> dict:
    """NATS server-level counters from /varz (#626: is NATS itself the bottleneck?)."""
    import requests

    r = requests.get(f"{nats_monitor.rstrip('/')}/varz", timeout=5)
    r.raise_for_status()
    v = r.json()
    return {"cpu": v.get("cpu"), "mem": v.get("mem"), "in_bytes": v.get("in_bytes"),
            "out_bytes": v.get("out_bytes"), "in_msgs": v.get("in_msgs"),
            "slow_consumers": v.get("slow_consumers"), "connections": v.get("connections")}


def proc_cpu_ticks(pid: int) -> int | None:
    try:
        with open(f"/proc/{pid}/stat") as f:
            return parse_proc_stat(f.read())
    except OSError:
        return None


def child_pids(parent: int) -> list[int]:
    """Direct children of ``parent`` (the load-generator pool processes) via /proc."""
    out = []
    for name in os.listdir("/proc"):
        if not name.isdigit():
            continue
        try:
            with open(f"/proc/{name}/stat") as f:
                txt = f.read()
            if int(txt[txt.rindex(")") + 2:].split()[1]) == parent:
                out.append(int(name))
        except (OSError, ValueError):
            continue
    return out


class Sampler(threading.Thread):
    """Samples JetStream pending, NATS /varz, per-replica worker CPU/RSS, host CPU and the load
    generator's own CPU every ``interval`` s until stopped.

    ``worker_pids`` is one pid per ConnectorWorker replica (#626): each gets ``worker_cpu_pct[i]`` /
    ``worker_rss_mb[i]`` in the sample, and the aggregate keys ``worker_cpu_pct`` (sum) /
    ``worker_rss_mb`` (max) keep the single-replica report shape.
    """

    def __init__(self, nats_monitor: str, worker_pids: list[int] | int | None, interval: float = 2.0):
        super().__init__(daemon=True)
        if isinstance(worker_pids, int):
            worker_pids = [worker_pids]
        self.nats_monitor, self.pids, self.interval = nats_monitor, list(worker_pids or []), interval
        self.stop_ev = threading.Event()
        self.samples: list[dict] = []
        self.hz = os.sysconf("SC_CLK_TCK")
        self._prev = None
        self._prev_varz = None
        self._self_pid = os.getpid()

    def _cpu(self) -> tuple[float, float, list[int | None], int]:
        with open("/proc/stat") as f:
            parts = [int(x) for x in f.readline().split()[1:]]
        host_busy = sum(parts) - parts[3] - parts[4]
        host_total = sum(parts)
        workers = [proc_cpu_ticks(p) for p in self.pids]
        loadgen = sum(t for t in (proc_cpu_ticks(p) for p in [self._self_pid] + child_pids(self._self_pid))
                      if t is not None)
        return host_busy, host_total, workers, loadgen

    def run(self):
        ncpu = os.cpu_count() or 1
        while not self.stop_ev.is_set():
            t = time.time()
            try:
                pending, msgs = jsz_pending(self.nats_monitor)
            except Exception:  # noqa: BLE001
                pending, msgs = -1, -1
            try:
                vz = varz(self.nats_monitor)
            except Exception:  # noqa: BLE001
                vz = None
            hb, ht, ws, lg = self._cpu()
            s = {"t": t, "pending": pending, "stream_msgs": msgs}
            if vz:
                s["nats_cpu_pct"] = vz["cpu"]
                s["nats_mem_mb"] = round((vz["mem"] or 0) / 1048576, 1)
                s["nats_slow_consumers"] = vz["slow_consumers"]
                s["nats_connections"] = vz["connections"]
            if self._prev:
                pt, phb, pht, pws, plg = self._prev
                dt = t - pt
                if dt > 0 and ht > pht:
                    s["host_cpu_pct"] = round(100.0 * (hb - phb) / (ht - pht) * ncpu, 1)
                    per = [round(100.0 * (w - pw) / self.hz / dt, 1) if w is not None and pw is not None else None
                           for w, pw in zip(ws, pws)]
                    if per:
                        s["worker_cpu_pct_by_replica"] = per
                        known = [x for x in per if x is not None]
                        if known:
                            s["worker_cpu_pct"] = round(sum(known), 1)
                    s["loadgen_cpu_pct"] = round(100.0 * (lg - plg) / self.hz / dt, 1)
                if vz and self._prev_varz and dt > 0:
                    s["nats_in_bytes_per_s"] = round(((vz["in_bytes"] or 0) - (self._prev_varz["in_bytes"] or 0)) / dt)
                    s["nats_in_msgs_per_s"] = round(((vz["in_msgs"] or 0) - (self._prev_varz["in_msgs"] or 0)) / dt)
            self._prev = (t, hb, ht, ws, lg)
            if vz:
                self._prev_varz = vz
            rss = []
            for p in self.pids:
                try:
                    with open(f"/proc/{p}/statm") as f:
                        rss.append(round(int(f.read().split()[1]) * 4096 / 1048576, 1))
                except OSError:
                    rss.append(None)
            if rss:
                s["worker_rss_mb_by_replica"] = rss
                known = [x for x in rss if x is not None]
                if known:
                    s["worker_rss_mb"] = max(known)
            self.samples.append(s)
            self.stop_ev.wait(self.interval)

    def stop(self):
        self.stop_ev.set()
        self.join(timeout=5)


# ── lake counting (boto3 + local duckdb) ──────────────────────────────────────────────────────────
class LakeCounter:
    def __init__(self, endpoint: str, bucket: str, building: str, cache_dir: str):
        import lake_s3_client as lc

        self.lc, self.endpoint, self.bucket, self.building = lc, endpoint, bucket, building
        self.prefix = f"building_id={building}/"
        self.cache = cache_dir
        self.rows: dict[str, int] = {}
        self.s3 = lc._client(endpoint, lc.DEFAULT_ACCESS_KEY, lc.DEFAULT_SECRET_KEY)

    def _local(self, key: str) -> str:
        p = os.path.join(self.cache, key.replace("/", "__"))
        if not os.path.exists(p):
            self.s3.download_file(self.bucket, key, p)
        return p

    def count(self) -> int:
        import duckdb

        keys = [k for k in self.lc.list_keys(self.endpoint, self.bucket, self.prefix) if k.endswith(".parquet")]
        con = duckdb.connect()
        for k in keys:
            if k in self.rows:
                continue
            try:
                self.rows[k] = con.execute(
                    "SELECT sum(num_rows) FROM parquet_file_metadata(?)", [self._local(k)]).fetchone()[0] or 0
            except Exception:  # noqa: BLE001 — partially written object: retry next poll
                continue
        return sum(v for k, v in self.rows.items() if k in set(keys))

    def duplicates(self) -> int:
        import duckdb

        files = [self._local(k) for k in self.rows]
        if not files:
            return 0
        con = duckdb.connect()
        return con.execute(
            "SELECT coalesce(sum(c - 1), 0) FROM (SELECT count(*) c FROM read_parquet(?, union_by_name=1) "
            "GROUP BY point_id, time HAVING count(*) > 1)", [files]).fetchone()[0]

    def purge(self):
        for k in list(self.rows):
            self.s3.delete_object(Bucket=self.bucket, Key=k)


# ── orchestration ─────────────────────────────────────────────────────────────────────────────────
def run_step(args, rate: int, idx: int, run_id: str, worker_pid: int | None) -> dict:
    import s10_pointlist_integrity as s10

    streams = args.streams or plan_streams(rate, args.max_streams)
    per_rate = split_rate(rate, streams)
    building = f"{run_id}-r{rate}"
    gws = [f"GW-{run_id}-{rate}-{i}" for i in range(streams)]
    pts = {gw: [f"{run_id}-{rate}-{i}-{j:04d}" for j in range(args.points_per_gateway)]
           for i, gw in enumerate(gws)}
    pb2, pb2g = s10.load_ingress_stubs()
    seeded: list[str] = []
    cache = tempfile.mkdtemp(prefix="s22-lake-")
    try:
        for gw in gws:
            for p in pts[gw]:
                s10.insert_point(args.oxigraph, p, gw, building)
                seeded.append(p)
        targets = [t.strip() for t in args.ingress.split(",") if t.strip()]
        tgt = {gw: targets[i % len(targets)] for i, gw in enumerate(gws)}
        for gw in gws:
            if not s10.wait_visible(pb2, pb2g, tgt[gw], gw, pts[gw][0], timeout_s=90):
                return {"target_rate": rate, "pass": False, "fail_reasons": ["seeded points not visible"]}

        sampler = Sampler(args.nats_monitor, worker_pid)
        sampler.start()
        start_at = time.time() + 3.0
        ctx = mp.get_context("spawn")
        nproc = max(1, min(streams, args.procs or args.max_streams))
        groups = [[(tgt[gw], gw, pts[gw], r) for j, (gw, r) in enumerate(zip(gws, per_rate)) if j % nproc == i]
                  for i in range(nproc)]
        with cf.ProcessPoolExecutor(max_workers=nproc, mp_context=ctx) as pool:
            futs = [pool.submit(load_worker, g, args.duration, start_at) for g in groups if g]
            res = [r for f in futs for r in f.result()]
        load_end = time.time()
        end_lag = next((s["pending"] for s in reversed(sampler.samples) if s["pending"] >= 0), -1)
        accepted = sum(r["accepted"] for r in res)
        sent = sum(r["sent"] for r in res)
        elapsed = max(r["elapsed_s"] for r in res)

        lake = LakeCounter(args.minio_endpoint, args.bucket, building, cache)
        rows, deadline = 0, load_end + args.drain_timeout
        while time.time() < deadline:
            rows = lake.count()
            if rows >= accepted:
                break
            time.sleep(5)
        drain_s = round(time.time() - load_end, 1)
        sampler.stop()
        dup = lake.duplicates() if rows else 0
        step = assess_step(rate, args.duration, accepted, elapsed, rows, dup, end_lag, args.lag_seconds)
        pend = [s["pending"] for s in sampler.samples if s["pending"] >= 0]
        win = [s for s in sampler.samples if start_at <= s["t"] <= load_end]  # CPU/RSS: load window only
        # #626: per-replica view — streams are assigned round-robin over --ingress targets, so each
        # target's accepted sum is that replica's share of the load.
        per_replica = {}
        for r in res:
            d = per_replica.setdefault(r["target"], {"streams": 0, "accepted": 0, "sent": 0})
            d["streams"] += 1
            d["accepted"] += r["accepted"]
            d["sent"] += r["sent"]
        for d in per_replica.values():
            d["achieved_rate"] = round(d["accepted"] / elapsed, 1) if elapsed > 0 else 0.0
        step.update({
            "streams": streams, "gateways": len(gws), "points": sum(len(v) for v in pts.values()),
            "sent": sent, "drain_s": drain_s,
            "peak_lag": max(pend) if pend else None,
            "worker_cpu_pct_p50": percentile([s["worker_cpu_pct"] for s in win if "worker_cpu_pct" in s], .5),
            "worker_cpu_pct_max": percentile([s["worker_cpu_pct"] for s in win if "worker_cpu_pct" in s], 1.0),
            "host_cpu_pct_p50": percentile([s["host_cpu_pct"] for s in win if "host_cpu_pct" in s], .5),
            "loadgen_cpu_pct_p50": percentile([s["loadgen_cpu_pct"] for s in win if "loadgen_cpu_pct" in s], .5),
            "nats_cpu_pct_p50": percentile([s["nats_cpu_pct"] for s in win if s.get("nats_cpu_pct") is not None], .5),
            "nats_in_bytes_per_s_p50": percentile([s["nats_in_bytes_per_s"] for s in win if "nats_in_bytes_per_s" in s], .5),
            "nats_slow_consumers_max": percentile([s["nats_slow_consumers"] for s in win if s.get("nats_slow_consumers") is not None], 1.0),
            "worker_rss_mb_max": percentile([s["worker_rss_mb"] for s in win if "worker_rss_mb" in s], 1.0),
            "worker_cpu_pct_p50_by_replica": [
                percentile([s["worker_cpu_pct_by_replica"][i] for s in win
                            if "worker_cpu_pct_by_replica" in s and s["worker_cpu_pct_by_replica"][i] is not None], .5)
                for i in range(len(sampler.pids))],
            "per_replica": per_replica,
            "lake_objects": len(lake.rows), "samples": sampler.samples,
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


def find_worker_pids(explicit: str | None) -> list[int]:
    """``--worker-pid`` accepts a comma-separated list (one per replica, #626); unset → every
    ConnectorWorker process on this host, in pid order."""
    if explicit:
        return [int(x) for x in explicit.split(",") if x.strip()]
    try:
        out = subprocess.check_output(["pgrep", "-f", "BuildingOS.ConnectorWorker.dll"], text=True)
        return sorted(int(x) for x in out.split())
    except Exception:  # noqa: BLE001
        return []


WORKER_ENV_KEYS = ("WORKER_ROLE", "GRPC_INGRESS_PUBLISH_WINDOW", "HOT_STORE_COALESCE_MS",
                   "HOT_STORE_COALESCE_MAX_POINTS", "PARQUET_FLUSH_INTERVAL", "PARQUET_FLUSH_MAX_ROWS",
                   "WARM_STORE", "GRPC_INGRESS_REQUIRE_HIERARCHY")


def worker_env(pid: int) -> dict[str, str]:
    """The ingest-relevant env of a (host-run) worker, read from /proc; empty for a container."""
    try:
        with open(f"/proc/{pid}/environ", "rb") as f:
            items = f.read().split(b"\0")
    except OSError:
        return {}
    env = dict(kv.decode(errors="replace").split("=", 1) for kv in items if b"=" in kv)
    return {k: env[k] for k in WORKER_ENV_KEYS if k in env}


def run_metadata(args, pids: list[int]) -> dict:
    """What the numbers were measured on (#626 §5.6): commit, host, args, worker env per replica."""
    repo = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
    try:
        commit = subprocess.check_output(["git", "-C", repo, "rev-parse", "--short", "HEAD"], text=True).strip()
    except Exception:  # noqa: BLE001
        commit = None
    return {
        "label": args.label, "commit": commit, "host": platform.node(), "cpu_count": os.cpu_count(),
        "python": platform.python_version(), "started_at": datetime.now(timezone.utc).isoformat(),
        "args": {k: getattr(args, k) for k in ("rates", "duration", "points_per_gateway", "streams", "procs",
                                                  "max_streams", "lag_seconds", "ingress", "nats_monitor")},
        "workers": [{"pid": p, "env": worker_env(p)} for p in pids],
    }


def render_md(run_id: str, args, steps: list[dict], knee: dict | None) -> str:
    meta = getattr(args, "_meta", None) or {}
    lines = [f"# S22 ingest rate sweep — {run_id}" + (f" ({meta['label']})" if meta.get("label") else ""), "",
             f"commit {meta.get('commit', '-')} · host {meta.get('host', '-')} · ingress {args.ingress} · "
             f"workers {', '.join(str(w['pid']) + ' ' + json.dumps(w['env']) for w in meta.get('workers', [])) or '-'}", "",
             f"duration/step {args.duration}s · points/gateway {args.points_per_gateway} · "
             f"cpu {os.cpu_count()} cores (load generator shares the host)", "",
             "| target/s | streams | achieved/s | accepted | lake rows | loss | dup | peak lag | end lag | drain s "
             "| worker CPU% p50/max | worker RSS MB | NATS CPU% p50 | loadgen CPU% p50 | per replica (accepted/s) | result |",
             "|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|---|---|"]
    for s in steps:
        lines.append(
            f"| {s['target_rate']} | {s.get('streams','-')} | {s.get('achieved_rate','-')} | {s.get('accepted','-')} "
            f"| {s.get('lake_rows','-')} | {s.get('loss_rate','-')} | {s.get('duplicate_rate','-')} "
            f"| {s.get('peak_lag','-')} | {s.get('end_lag','-')} | {s.get('drain_s','-')} "
            f"| {s.get('worker_cpu_pct_p50','-')}/{s.get('worker_cpu_pct_max','-')} | {s.get('worker_rss_mb_max','-')} "
            f"| {s.get('nats_cpu_pct_p50','-')} | {s.get('loadgen_cpu_pct_p50','-')} "
            f"| {' / '.join(t + ': ' + str(d['achieved_rate']) for t, d in (s.get('per_replica') or {}).items()) or '-'} "
            f"| {'PASS' if s['pass'] else 'FAIL: ' + '; '.join(s['fail_reasons'])} |")
    lines += ["", f"**Knee:** {'none within the swept range' if not knee else 'first failing step = ' + str(knee['target_rate']) + ' frames/s'}"]
    ok = [s["achieved_rate"] for s in steps if s["pass"]]
    if ok:
        lines.append(f"**Highest passing sustained rate:** {max(ok):.0f} frames/s")
    return "\n".join(lines) + "\n"


def main() -> int:
    ap = argparse.ArgumentParser(description="S22 gRPC ingest rate sweep")
    ap.add_argument("--out", default="results/S22")
    ap.add_argument("--rates", default="500,1000,2000,5000,10000,20000")
    ap.add_argument("--duration", type=int, default=60)
    ap.add_argument("--points-per-gateway", type=int, default=100)
    ap.add_argument("--streams", type=int, default=0, help="force stream count (0 = auto)")
    ap.add_argument("--procs", type=int, default=0, help="load-generator processes (0 = max-streams)")
    ap.add_argument("--max-streams", type=int, default=max(1, (os.cpu_count() or 2) - 1))
    ap.add_argument("--lag-seconds", type=float, default=5.0)
    ap.add_argument("--drain-timeout", type=int, default=240)
    ap.add_argument("--continue-on-failure", action="store_true")
    ap.add_argument("--keep-lake", action="store_true")
    ap.add_argument("--ingress", default=os.environ.get("INGRESS_TARGET", "localhost:5051"),
                    help="host:port; comma-separate several ConnectorWorker replicas (streams go round-robin)")
    ap.add_argument("--oxigraph", default=os.environ.get("OXIGRAPH_URL", "http://localhost:7878"))
    ap.add_argument("--minio-endpoint", default=os.environ.get("MINIO_ENDPOINT_HOST", "localhost:9000"))
    ap.add_argument("--bucket", default=os.environ.get("LAKE_BUCKET", "cold"))
    ap.add_argument("--nats-monitor", default=os.environ.get("NATS_MONITOR_URL", "http://localhost:8222"))
    ap.add_argument("--worker-pid", default=None,
                    help="ConnectorWorker pid(s), comma-separated one per replica (default: every worker on this host)")
    ap.add_argument("--label", default=None, help="free-text tag stored in sweep.json (e.g. baseline / after-defaults)")
    args = ap.parse_args()

    run_id = "s22" + datetime.now(timezone.utc).strftime("%m%d%H%M%S")
    out = os.path.join(args.out, run_id)
    os.makedirs(out, exist_ok=True)
    pid = find_worker_pids(args.worker_pid)
    meta = run_metadata(args, pid)
    print(f"[s22] run={run_id} worker_pids={pid} rates={args.rates} commit={meta['commit']} label={args.label}")

    steps, knee = [], None
    for i, rate in enumerate(parse_rates(args.rates)):
        print(f"[s22] step {i + 1}: target {rate}/s for {args.duration}s ...", flush=True)
        step = run_step(args, rate, i, run_id, pid)
        steps.append(step)
        print(f"[s22]   -> {'PASS' if step['pass'] else 'FAIL'} achieved={step.get('achieved_rate')} "
              f"rows={step.get('lake_rows')}/{step.get('accepted')} lag_end={step.get('end_lag')} "
              f"drain={step.get('drain_s')}s {step.get('fail_reasons') or ''}", flush=True)
        with open(os.path.join(out, "sweep.json"), "w") as f:
            json.dump({"run_id": run_id, "meta": meta, "steps": steps}, f, indent=2)
        if not step["pass"] and knee is None:
            knee = step
            if not args.continue_on_failure:
                break
    args._meta = meta
    md = render_md(run_id, args, [{k: v for k, v in s.items() if k != "samples"} for s in steps], knee)
    with open(os.path.join(out, "report.md"), "w") as f:
        f.write(md)
    print(md)
    print(f"[s22] wrote {out}")
    return 0 if knee is None else 1


if __name__ == "__main__":
    sys.exit(main())
