#!/usr/bin/env python3
"""S22 companion: the single-stream ceiling measured with the native .NET load generator (#625 G1).

The Python harness (s22_ingest_rate_sweep.py) drives one stream per asyncio task and saturates a CPU
core at roughly 2.5-3k frames/s per process — enough to find a 1.5k serial ceiling, not enough to
tell whether a pipelined stream stops at 3k because of the worker or because of the generator. This
script seeds the points the same way (s10 fixtures), then drives ONE stream with
``ingress-loadgen-dotnet`` (Grpc.Net.Client) while sampling the worker/NATS/host like the sweep, and
writes ``single_stream.json`` + ``report.md``. Loss/duplicates are not re-counted here (the Python
sweep already proves them 0 at these rates); the question is only the accepted rate per stream.

Usage:
  dotnet build -c Release Tools/e2e-performance/ingress-loadgen-dotnet
  python s22_single_stream_dotnet.py --out results/S22-single --rates 2000,5000,8000,12000 --duration 60 \
      [--ingress localhost:5051] [--oxigraph http://localhost:7878] [--nats-monitor http://localhost:8222] \
      [--worker-pid N[,N2]] [--label after-defaults] [--points 100]
"""
from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import time
from datetime import datetime, timezone

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, HERE)
import s10_pointlist_integrity as s10  # noqa: E402
import s22_ingest_rate_sweep as s22  # noqa: E402

LOADGEN_DIR = os.path.join(HERE, "ingress-loadgen-dotnet")


def loadgen_cmd() -> list[str]:
    dll = os.path.join(LOADGEN_DIR, "bin", "Release", "net8.0", "IngressLoadGen.dll")
    if not os.path.exists(dll):
        raise SystemExit(f"build the load generator first: dotnet build -c Release {LOADGEN_DIR}")
    return ["dotnet", dll]


def run_rate(args, rate: int, run_id: str, pids: list[int]) -> dict:
    gw = f"GW-{run_id}-{rate}"
    building = f"{run_id}-r{rate}"
    pts = [f"{run_id}-{rate}-{j:04d}" for j in range(args.points)]
    pb2, pb2g = s10.load_ingress_stubs()
    for p in pts:
        s10.insert_point(args.oxigraph, p, gw, building)
    try:
        if not s10.wait_visible(pb2, pb2g, args.ingress, gw, pts[0], timeout_s=90):
            return {"target_rate": rate, "pass": False, "fail_reasons": ["seeded points not visible"]}
        sampler = s22.Sampler(args.nats_monitor, pids)
        sampler.start()
        t0 = time.time()
        proc = subprocess.run(loadgen_cmd() + ["--target", args.ingress, "--gateway", gw, "--points", ",".join(pts),
                                              "--rate", str(rate), "--duration", str(args.duration), "--streams", "1"],
                              capture_output=True, text=True)
        t1 = time.time()
        sampler.stop()
        lines = [json.loads(l) for l in proc.stdout.splitlines() if l.startswith("{")]
        total = next((l for l in lines if l.get("stream") == "total"), None)
        if proc.returncode not in (0, 2) or total is None:
            return {"target_rate": rate, "pass": False, "fail_reasons": [f"loadgen rc={proc.returncode}: {proc.stderr[-400:]}"]}
        win = [s for s in sampler.samples if t0 + 2 <= s["t"] <= t1]
        ratio = total["achieved_rate"] / rate
        step = {
            "target_rate": rate, "duration_s": args.duration, "sent": total["sent"], "accepted": total["accepted"],
            "achieved_rate": total["achieved_rate"], "achieved_ratio": round(ratio, 4),
            "refused": total["sent"] - total["accepted"],
            "pass": ratio >= s22.ACHIEVED_MIN and total["sent"] == total["accepted"],
            "fail_reasons": ([] if ratio >= s22.ACHIEVED_MIN else [f"achieved {ratio:.1%} < {s22.ACHIEVED_MIN:.0%} of target"])
                            + ([] if total["sent"] == total["accepted"] else ["frames refused by the worker"]),
            "peak_lag": max((s["pending"] for s in win if s["pending"] >= 0), default=None),
            "worker_cpu_pct_p50": s22.percentile([s["worker_cpu_pct"] for s in win if "worker_cpu_pct" in s], .5),
            "worker_cpu_pct_max": s22.percentile([s["worker_cpu_pct"] for s in win if "worker_cpu_pct" in s], 1.0),
            "worker_rss_mb_max": s22.percentile([s["worker_rss_mb"] for s in win if "worker_rss_mb" in s], 1.0),
            "loadgen_cpu_pct_p50": s22.percentile([s["loadgen_cpu_pct"] for s in win if "loadgen_cpu_pct" in s], .5),
            "nats_cpu_pct_p50": s22.percentile([s["nats_cpu_pct"] for s in win if s.get("nats_cpu_pct") is not None], .5),
            "host_cpu_pct_p50": s22.percentile([s["host_cpu_pct"] for s in win if "host_cpu_pct" in s], .5),
            "samples": sampler.samples,
        }
        return step
    finally:
        for p in pts:
            try:
                s10.delete_point(args.oxigraph, p)
            except Exception:  # noqa: BLE001
                pass


def render_md(run_id: str, meta: dict, steps: list[dict]) -> str:
    lines = [f"# S22 single-stream ceiling (.NET load generator) — {run_id}" + (f" ({meta.get('label')})" if meta.get("label") else ""),
             "", f"commit {meta.get('commit')} · host {meta.get('host')} · ingress {meta['args']['ingress']} · "
             f"workers {', '.join(str(w['pid']) + ' ' + json.dumps(w['env']) for w in meta.get('workers', [])) or '-'}", "",
             "| target/s | achieved/s | ratio | sent | accepted | peak lag | worker CPU% p50/max | worker RSS MB | loadgen CPU% p50 | NATS CPU% p50 | result |",
             "|--:|--:|--:|--:|--:|--:|--:|--:|--:|--:|---|"]
    for s in steps:
        lines.append(f"| {s['target_rate']} | {s.get('achieved_rate','-')} | {s.get('achieved_ratio','-')} | {s.get('sent','-')} "
                     f"| {s.get('accepted','-')} | {s.get('peak_lag','-')} | {s.get('worker_cpu_pct_p50','-')}/{s.get('worker_cpu_pct_max','-')} "
                     f"| {s.get('worker_rss_mb_max','-')} | {s.get('loadgen_cpu_pct_p50','-')} | {s.get('nats_cpu_pct_p50','-')} "
                     f"| {'PASS' if s['pass'] else 'FAIL: ' + '; '.join(s['fail_reasons'])} |")
    return "\n".join(lines) + "\n"


def main() -> int:
    ap = argparse.ArgumentParser(description="S22 single-stream ceiling with the .NET load generator")
    ap.add_argument("--out", default="results/S22-single")
    ap.add_argument("--rates", default="2000,5000,8000,12000")
    ap.add_argument("--duration", type=int, default=60)
    ap.add_argument("--points", type=int, default=100)
    ap.add_argument("--ingress", default=os.environ.get("INGRESS_TARGET", "localhost:5051"))
    ap.add_argument("--oxigraph", default=os.environ.get("OXIGRAPH_URL", "http://localhost:7878"))
    ap.add_argument("--nats-monitor", default=os.environ.get("NATS_MONITOR_URL", "http://localhost:8222"))
    ap.add_argument("--worker-pid", default=None)
    ap.add_argument("--label", default=None)
    ap.add_argument("--continue-on-failure", action="store_true")
    args = ap.parse_args()
    args.streams, args.procs, args.max_streams, args.lag_seconds, args.points_per_gateway = 1, 1, 1, None, args.points
    run_id = "s22s" + datetime.now(timezone.utc).strftime("%m%d%H%M%S")
    out = os.path.join(args.out, run_id)
    os.makedirs(out, exist_ok=True)
    pids = s22.find_worker_pids(args.worker_pid)
    meta = s22.run_metadata(args, pids)
    print(f"[s22-single] run={run_id} worker_pids={pids} rates={args.rates} label={args.label}", flush=True)
    steps = []
    for rate in s22.parse_rates(args.rates):
        print(f"[s22-single] target {rate}/s for {args.duration}s ...", flush=True)
        step = run_rate(args, rate, run_id, pids)
        steps.append(step)
        print(f"[s22-single]   -> {'PASS' if step['pass'] else 'FAIL'} achieved={step.get('achieved_rate')} "
              f"accepted={step.get('accepted')}/{step.get('sent')} {step.get('fail_reasons') or ''}", flush=True)
        with open(os.path.join(out, "single_stream.json"), "w") as f:
            json.dump({"run_id": run_id, "meta": meta, "steps": steps}, f, indent=2)
        if not step["pass"] and not args.continue_on_failure:
            break
    md = render_md(run_id, meta, [{k: v for k, v in s.items() if k != "samples"} for s in steps])
    with open(os.path.join(out, "report.md"), "w") as f:
        f.write(md)
    print(md)
    print(f"[s22-single] wrote {out}")
    return 0 if all(s["pass"] for s in steps) else 1


if __name__ == "__main__":
    sys.exit(main())
