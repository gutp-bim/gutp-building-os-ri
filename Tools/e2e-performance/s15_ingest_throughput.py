#!/usr/bin/env python3
"""E1 — gRPC GatewayIngress 持続スループット負荷クライアント (#239).

A sustained-rate load client for the canonical gRPC ingest path: streams TelemetryFrame at a target
rate for a duration, then runs quality_checker (parquet mode) over this run's building to compute the
E1 KPIs (loss / duplicate / sustained-throughput / validation-error). normalize_quality wires the
result into the KPI gate.

Flow: seed K points (own building) → sustained gRPC stream (rate × duration) → flush wait →
quality_checker --mode parquet --building <run> --expected <accepted> → quality-check-result.json.

Usage:
  python s15_ingest_throughput.py --out results/E1 [--rate 200] [--duration 30] [--points 20]
      [--ingress localhost:5051] [--oxigraph http://localhost:7878]
      [--minio-endpoint localhost:9000] [--flush-wait 80]
"""

from __future__ import annotations

import argparse
import asyncio
import json
import os
import subprocess
import sys
import time
from datetime import datetime, timezone

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import s10_pointlist_integrity as s10  # noqa: E402


async def stream_at_rate(pb2, pb2g, target: str, gw: str, points: list[str],
                         rate: int, duration: int) -> tuple[int, float]:
    """Stream frames at ~rate/s for duration s on one client-stream. Returns (accepted, elapsed_s)."""
    import grpc  # type: ignore

    interval = 1.0 / rate if rate > 0 else 0.0
    total = rate * duration
    t0 = time.perf_counter()

    async def gen():
        for i in range(total):
            p = points[i % len(points)]
            yield pb2.TelemetryFrame(gateway_id=gw, point_id=p, value=20.0 + (i % 100) / 10.0,
                                     timestamp=datetime.now(timezone.utc).isoformat())
            if interval:
                await asyncio.sleep(interval)

    async with grpc.aio.insecure_channel(target) as ch:
        ack = await pb2g.GatewayIngressStub(ch).StreamTelemetry(gen())
    return int(ack.accepted), time.perf_counter() - t0


def run_quality_checker(run_id: str, building: str, expected: int, minio_endpoint: str) -> dict | None:
    perf = os.path.dirname(os.path.abspath(__file__))
    py = os.path.join(perf, ".venv", "bin", "python")
    py = py if os.path.exists(py) else sys.executable
    cmd = [py, os.path.join(perf, "quality_checker.py"),
           "--run-id", run_id, "--building", building, "--expected", str(expected),
           "--mode", "parquet", "--minio-endpoint", minio_endpoint]
    subprocess.run(cmd, check=False, capture_output=True, text=True, timeout=180)
    result_path = os.path.join(perf, "results", run_id, "quality-check-result.json")
    if os.path.isfile(result_path):
        with open(result_path) as f:
            return json.load(f)
    return None


async def run(args) -> int:
    pb2, pb2g = s10.load_ingress_stubs()
    tag = datetime.now(timezone.utc).strftime("%Y%m%d%H%M%S")
    run_id = f"thr-{tag}"
    gw, building = f"GW-THR-{tag}", run_id
    points = [f"thrpt-{tag}-{i:04d}" for i in range(args.points)]
    seeded: list[str] = []
    try:
        for p in points:
            s10.insert_point(args.oxigraph, p, gw, building); seeded.append(p)
        if not s10.wait_visible(pb2, pb2g, args.ingress, gw, points[0]):
            print("seeded points not visible — aborting", file=sys.stderr); return 2

        print(f"streaming ~{args.rate}/s for {args.duration}s (~{args.rate * args.duration} frames)...")
        accepted, elapsed = await stream_at_rate(pb2, pb2g, args.ingress, gw, points, args.rate, args.duration)
        achieved = accepted / elapsed if elapsed else 0.0
        print(f"accepted={accepted} elapsed={elapsed:.1f}s achieved_rate={achieved:.0f}/s; "
              f"waiting {args.flush_wait}s for flush...")
        await asyncio.sleep(args.flush_wait)

        qc = run_quality_checker(run_id, building, accepted, args.minio_endpoint)
        if qc is None:
            print("quality_checker produced no result — aborting", file=sys.stderr); return 2

        loss = float(qc.get("loss_rate", 0.0))
        dup = float(qc.get("duplicate_rate", 0.0))
        invalid = int(qc.get("schema_invalid_count", 0))
        rows = int(qc.get("db_row_count", 0))
        metrics = {
            "sustained_throughput_ratio": round(max(0.0, 1.0 - loss), 5),
            "loss_rate": round(loss, 6),
            "duplicate_rate": round(dup, 6),
            "validation_error_rate": round(invalid / max(rows, 1), 6),
        }
        result = {
            "axis": "E1_ingest_throughput",
            "generated_at": datetime.now(timezone.utc).isoformat(),
            "config": {"target_rate": args.rate, "duration_s": args.duration,
                       "achieved_rate": round(achieved, 1), "accepted": accepted, "lake_rows": rows},
            "metrics": metrics,
            "note": "gRPC GatewayIngress 持続負荷 → quality_checker(parquet, building filter)。",
        }
        os.makedirs(args.out, exist_ok=True)
        out_path = os.path.join(args.out, "E1-throughput.json")
        with open(out_path, "w") as f:
            json.dump(result, f, indent=2)

        print("E1 throughput results:")
        for k, (v, thr, ok) in {
            "sustained_throughput_ratio": (metrics["sustained_throughput_ratio"], ">=0.99",
                                           metrics["sustained_throughput_ratio"] >= 0.99),
            "loss_rate": (metrics["loss_rate"], "<=0.01", metrics["loss_rate"] <= 0.01),
            "duplicate_rate": (metrics["duplicate_rate"], "<=0.005", metrics["duplicate_rate"] <= 0.005),
            "validation_error_rate": (metrics["validation_error_rate"], "<=0.01",
                                      metrics["validation_error_rate"] <= 0.01),
        }.items():
            print(f"  {k:28} {v!s:10} {thr:9} -> {'PASS' if ok else 'FAIL'}")
        print(f"Wrote {out_path}")
        hard_fail = metrics["sustained_throughput_ratio"] < 0.99 or metrics["loss_rate"] > 0.01
        return 1 if hard_fail else 0
    finally:
        for p in seeded:
            try:
                s10.delete_point(args.oxigraph, p)
            except Exception:  # noqa: BLE001
                pass
        print(f"cleaned up {len(seeded)} seeded points")


def main() -> int:
    ap = argparse.ArgumentParser(description="E1 gRPC ingress sustained-throughput load client (#239)")
    ap.add_argument("--out", default="results/E1")
    ap.add_argument("--rate", type=int, default=200, help="target frames/sec")
    ap.add_argument("--duration", type=int, default=30, help="seconds")
    ap.add_argument("--points", type=int, default=20)
    ap.add_argument("--ingress", default=os.environ.get("INGRESS_TARGET", "localhost:5051"))
    ap.add_argument("--oxigraph", default=os.environ.get("OXIGRAPH_URL", "http://localhost:7878"))
    ap.add_argument("--minio-endpoint", default=os.environ.get("MINIO_ENDPOINT_HOST", "localhost:9000"))
    ap.add_argument("--flush-wait", type=int, default=80)
    args = ap.parse_args()
    return asyncio.run(run(args))


if __name__ == "__main__":
    sys.exit(main())
