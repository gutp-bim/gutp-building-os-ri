#!/usr/bin/env python3
"""E2 — Ingest E2E latency / 鮮度 (#243 sibling, Epic #238).

Measures the time from a gateway producing a reading to Building OS treating it as *validated*
telemetry, plus the Parquet write-freshness lag:

  * ingest E2E latency p50/p95/p99 — gen ts (embedded in the gRPC TelemetryFrame.timestamp, which the
    ingress copies verbatim into the validated entity's ``datetime``) → the moment a NATS subscriber on
    ``building-os.validated.telemetry`` receives it. A core-NATS subscriber sees JetStream-published
    messages live, so no durable consumer is needed.
  * parquet freshness p95 — scraped from Prometheus
    (``building_os_parquet_writer_freshness_lag_seconds`` histogram, #213): now − newest event time at
    flush. Pass/fail threshold is dynamic (flush interval + 60s).

Reuses the E5 ingress plumbing (runtime-compiled proto, SPARQL twin seed/cleanup) from
``s10_pointlist_integrity``. Frames are tagged with a unique ``building`` so the subscriber counts only
this run's messages.

Usage:
  python s11_ingest_latency.py --out results/E2 [--frames 600] [--rate 20]
      [--ingress localhost:5051] [--nats nats://localhost:4222] [--oxigraph http://localhost:7878]
      [--prometheus http://localhost:9090] [--flush-interval-min 1]
"""

from __future__ import annotations

import argparse
import asyncio
import json
import logging
import os
import sys
import time
import urllib.parse
import urllib.request
from datetime import datetime, timezone

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import s10_pointlist_integrity as s10  # noqa: E402  (shared ingress/seed plumbing)

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
logger = logging.getLogger("e2")


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


def percentile(sorted_vals: list[float], q: float) -> float:
    if not sorted_vals:
        return float("nan")
    return sorted_vals[min(len(sorted_vals) - 1, int(len(sorted_vals) * q))]


def query_prometheus(prom: str, expr: str) -> float | None:
    url = f"{prom.rstrip('/')}/api/v1/query?query={urllib.parse.quote(expr)}"
    try:
        with urllib.request.urlopen(url, timeout=10) as resp:  # noqa: S310
            doc = json.loads(resp.read())
        result = doc.get("data", {}).get("result", [])
        if not result:
            return None
        val = float(result[0]["value"][1])
        return None if val != val else val  # NaN guard
    except Exception as e:  # noqa: BLE001
        logger.warning("prometheus query failed: %s", e)
        return None


async def run(args) -> int:
    import grpc  # type: ignore
    import nats  # type: ignore

    pb2, pb2_grpc = s10.load_ingress_stubs()

    tag = datetime.now(timezone.utc).strftime("%Y%m%d%H%M%S")
    gw = f"GW-E2-{tag}"
    building = f"e2-{tag}"
    n_points = 50
    points = [f"e2pt-{tag}-{i:04d}" for i in range(n_points)]
    frames = args.frames
    interval = 1.0 / args.rate if args.rate > 0 else 0.0

    latencies: list[float] = []
    received = 0

    seeded: list[str] = []
    nc = None
    try:
        logger.info("Seeding %d points (gw=%s building=%s)", n_points, gw, building)
        for p in points:
            s10.insert_point(args.oxigraph, p, gw, building)
            seeded.append(p)
        # Make the points visible to the ingress metadata cache before timing anything.
        if not s10.wait_visible(pb2, pb2_grpc, args.ingress, gw, points[0]):
            logger.error("seeded points never became visible to ingress cache — aborting")
            return 2

        nc = await nats.connect(args.nats)

        async def on_msg(msg):
            nonlocal received
            recv = time.time()
            for row in _extract_rows(json.loads(msg.data)):
                if row.get("building") != building:
                    continue
                gen = _parse_iso_epoch(row.get("datetime"))
                if gen is not None:
                    latencies.append((recv - gen) * 1000.0)
                    received += 1

        sub = await nc.subscribe("building-os.validated.telemetry", cb=on_msg)

        logger.info("Streaming %d frames at ~%d/s (interval %.3fs)", frames, args.rate, interval)
        sent = 0

        async def gen():
            nonlocal sent
            for i in range(frames):
                pid = points[i % n_points]
                yield pb2.TelemetryFrame(
                    gateway_id=gw, point_id=pid, value=20.0 + (i % 100) / 10.0,
                    timestamp=datetime.now(timezone.utc).isoformat())
                sent += 1
                if interval:
                    await asyncio.sleep(interval)

        async with grpc.aio.insecure_channel(args.ingress) as ch:
            stub = pb2_grpc.GatewayIngressStub(ch)
            ack = await stub.StreamTelemetry(gen())
        accepted = int(ack.accepted)
        logger.info("sent=%d accepted=%d; draining subscriber...", sent, accepted)

        # Drain: wait until receipts settle (or grace timeout) so late messages count.
        grace_deadline = time.time() + 10
        last = -1
        while time.time() < grace_deadline:
            await asyncio.sleep(0.5)
            if received == last and received >= accepted:
                break
            last = received
        await sub.unsubscribe()

        latencies.sort()
        p50 = percentile(latencies, 0.50)
        p95 = percentile(latencies, 0.95)
        p99 = percentile(latencies, 0.99)

        # Parquet freshness: wait for ≥2 flush cycles so rate() over the histogram buckets is
        # computable (a single flush sample makes rate()/histogram_quantile NaN).
        flush_wait = int(args.flush_interval_min) * 60 * 2 + 25
        logger.info("waiting %ds for ≥2 Parquet flushes to record freshness_lag...", flush_wait)
        await asyncio.sleep(flush_wait)
        freshness_p95_s = query_prometheus(
            args.prometheus,
            "histogram_quantile(0.95, sum(rate("
            "building_os_parquet_writer_freshness_lag_seconds_bucket[10m])) by (le))")
        freshness_method = "p95"
        if freshness_p95_s is None:
            # Fallback for low flush volume: average lag = sum/count (dedup-safe — duplicate scrape
            # series scale numerator and denominator equally).
            freshness_p95_s = query_prometheus(
                args.prometheus,
                "sum(building_os_parquet_writer_freshness_lag_seconds_sum) / "
                "sum(building_os_parquet_writer_freshness_lag_seconds_count)")
            freshness_method = "avg(sum/count) fallback"
        freshness_threshold_s = int(args.flush_interval_min) * 60 + 60

        result = {
            "axis": "E2_ingest_latency",
            "generated_at": datetime.now(timezone.utc).isoformat(),
            "config": {"frames": frames, "rate_per_s": args.rate, "points": n_points,
                       "flush_interval_min": args.flush_interval_min},
            "counts": {"sent": sent, "accepted": accepted, "received": received},
            "metrics": {
                "ingest_e2e_p50_ms": round(p50, 1),
                "ingest_e2e_p95_ms": round(p95, 1),
                "ingest_e2e_p99_ms": round(p99, 1),
                "parquet_freshness_p95_s": round(freshness_p95_s, 1) if freshness_p95_s is not None else None,
            },
            "thresholds": {
                "ingest_e2e_p95_ms": "< 2000",
                "parquet_freshness_p95_s": f"<= {freshness_threshold_s} (flush {args.flush_interval_min}min + 60s)",
            },
            "freshness_method": freshness_method,
        }

        def ok(name, val, pred):
            status = "PASS" if pred else ("SKIP" if val is None else "FAIL")
            logger.info("  %-26s %-10s -> %s", name, val, status)
            return status

        logger.info("E2 results (sent=%d accepted=%d received=%d):", sent, accepted, received)
        statuses = {
            "ingest_e2e_p95_ms": ok("ingest_e2e_p95_ms", result["metrics"]["ingest_e2e_p95_ms"],
                                    p95 == p95 and p95 < 2000),
            "parquet_freshness_p95_s": ok("parquet_freshness_p95_s", result["metrics"]["parquet_freshness_p95_s"],
                                          freshness_p95_s is not None and freshness_p95_s <= freshness_threshold_s),
        }
        result["status"] = statuses
        os.makedirs(args.out, exist_ok=True)
        out_path = os.path.join(args.out, "E2-ingest-latency.json")
        with open(out_path, "w") as f:
            json.dump(result, f, indent=2)
        logger.info("Wrote %s", out_path)

        return 1 if statuses["ingest_e2e_p95_ms"] == "FAIL" else 0
    finally:
        if nc is not None:
            await nc.drain()
        for p in seeded:
            try:
                s10.delete_point(args.oxigraph, p)
            except Exception:  # noqa: BLE001
                pass
        logger.info("cleaned up %d seeded points", len(seeded))


def main() -> int:
    ap = argparse.ArgumentParser(description="E2 ingest E2E latency + Parquet freshness harness")
    ap.add_argument("--out", default="results/E2")
    ap.add_argument("--frames", type=int, default=600)
    ap.add_argument("--rate", type=int, default=20, help="frames per second")
    ap.add_argument("--ingress", default=os.environ.get("INGRESS_TARGET", "localhost:5051"))
    ap.add_argument("--nats", default=os.environ.get("NATS_URL", "nats://localhost:4222"))
    ap.add_argument("--oxigraph", default=os.environ.get("OXIGRAPH_URL", "http://localhost:7878"))
    ap.add_argument("--prometheus", default=os.environ.get("PROMETHEUS_URL", "http://localhost:9090"))
    ap.add_argument("--flush-interval-min", type=int,
                    default=int(os.environ.get("PARQUET_FLUSH_INTERVAL", "1")))
    args = ap.parse_args()
    return asyncio.run(run(args))


if __name__ == "__main__":
    sys.exit(main())
