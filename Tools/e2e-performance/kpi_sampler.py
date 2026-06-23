#!/usr/bin/env python3
"""KPI sampler for the production-scale Parquet run (#297).

Polls, every --interval seconds while a load run is in flight, the two backpressure/freshness
signals the architecture review asked for and that the existing harnesses do NOT capture over time:

  1. ParquetLakeWriter consumer pending  — from NATS monitoring (:8222 /jsz). Must NOT grow
     monotonically (writer keeps up with ingest).
  2. parquet_writer.freshness_lag p95     — from Prometheus (optional). Should stay
     <= PARQUET_FLUSH_INTERVAL + 60s.

Each tick appends one JSON line to <out>/kpi-timeseries.jsonl. On stop (duration elapsed or
SIGINT/SIGTERM) it writes <out>/kpi-summary.json with:
  - total_pending samples + a linear-regression slope over the second half (pending/sec)
  - pending_stable: slope <= --pending-slope-max
  - freshness_lag_p95_ms (last non-null Prometheus reading)

NATS :8222 is the reliable source (the OSS compose exposes it; no nats-exporter required).
Prometheus is optional — when --prometheus is unreachable, prom fields are null and the run still
produces the pending KPI.
"""
from __future__ import annotations

import argparse
import json
import os
import signal
import sys
import time
from pathlib import Path

import requests

_stop = False


def _on_signal(signum, frame):  # noqa: ARG001
    global _stop
    _stop = True


def _walk_consumers(node):
    """Yield (stream, consumer, num_pending) from an arbitrarily nested /jsz tree."""
    if isinstance(node, dict):
        if "num_pending" in node and ("name" in node or "stream_name" in node):
            yield (
                node.get("stream_name", node.get("_stream", "?")),
                node.get("name", "?"),
                int(node.get("num_pending", 0)),
            )
        for k, v in node.items():
            # tag stream name onto child consumer_detail entries
            if k == "consumer_detail" and isinstance(v, list):
                stream = node.get("name", "?")
                for c in v:
                    if isinstance(c, dict):
                        c.setdefault("_stream", stream)
            yield from _walk_consumers(v)
    elif isinstance(node, list):
        for item in node:
            yield from _walk_consumers(item)


def sample_pending(nats_url: str, stream_filter: str) -> tuple[int, dict[str, int]]:
    """Return (total_pending, {consumer: pending}) for consumers whose stream matches the filter."""
    r = requests.get(f"{nats_url.rstrip('/')}/jsz?consumers=1&streams=1", timeout=5)
    r.raise_for_status()
    per: dict[str, int] = {}
    for stream, consumer, pending in _walk_consumers(r.json()):
        if stream_filter and stream_filter.upper() not in str(stream).upper():
            continue
        per[f"{stream}/{consumer}"] = pending
    return sum(per.values()), per


def prom_instant(prom_url: str, query: str) -> float | None:
    try:
        r = requests.get(
            f"{prom_url.rstrip('/')}/api/v1/query", params={"query": query}, timeout=8
        )
        r.raise_for_status()
        result = r.json().get("data", {}).get("result", [])
        if not result:
            return None
        return float(result[0]["value"][1])
    except (requests.RequestException, KeyError, ValueError, IndexError):
        return None


def _slope(xs: list[float], ys: list[float]) -> float:
    """Least-squares slope of ys vs xs (0 if degenerate)."""
    n = len(xs)
    if n < 2:
        return 0.0
    mx = sum(xs) / n
    my = sum(ys) / n
    denom = sum((x - mx) ** 2 for x in xs)
    if denom == 0:
        return 0.0
    return sum((x - mx) * (y - my) for x, y in zip(xs, ys)) / denom


def main() -> int:
    ap = argparse.ArgumentParser(description="Production-run KPI sampler (#297)")
    ap.add_argument("--out", required=True, help="results dir (kpi-timeseries.jsonl / kpi-summary.json)")
    ap.add_argument("--interval", type=int, default=15, help="poll seconds (default 15)")
    ap.add_argument("--duration", type=int, default=0, help="stop after N seconds (0 = until signal)")
    ap.add_argument("--nats", default=os.environ.get("NATS_MONITOR_URL", "http://localhost:8222"))
    ap.add_argument("--stream-filter", default="VALIDATED", help="substring match on stream name")
    ap.add_argument("--prometheus", default=os.environ.get("PROMETHEUS_URL", ""))
    ap.add_argument(
        "--lag-metric",
        default="building_os_parquet_writer_freshness_lag",
        help="Prometheus histogram base name for freshness lag",
    )
    ap.add_argument("--flush-interval-min", type=int, default=5, help="for the lag KPI threshold note")
    ap.add_argument("--pending-slope-max", type=float, default=1.0, help="pending/sec slope ceiling")
    args = ap.parse_args()

    out = Path(args.out)
    out.mkdir(parents=True, exist_ok=True)
    ts_path = out / "kpi-timeseries.jsonl"

    signal.signal(signal.SIGINT, _on_signal)
    signal.signal(signal.SIGTERM, _on_signal)

    lag_query = (
        f"histogram_quantile(0.95, sum(rate({args.lag_metric}_bucket[5m])) by (le))"
        if args.prometheus
        else ""
    )
    rows_query = f"sum({args.lag_metric.rsplit('_freshness_lag', 1)[0]}_rows_total)" if args.prometheus else ""

    start = time.monotonic()
    samples: list[dict] = []
    print(f"[kpi] sampling every {args.interval}s → {ts_path} (nats={args.nats}, prom={args.prometheus or 'off'})")
    with ts_path.open("a") as fh:
        while not _stop:
            elapsed = round(time.monotonic() - start, 1)
            try:
                total_pending, per = sample_pending(args.nats, args.stream_filter)
            except requests.RequestException as e:
                total_pending, per = -1, {"error": str(e)}  # type: ignore[dict-item]
            rec = {
                "ts": time.strftime("%Y-%m-%dT%H:%M:%SZ", time.gmtime()),
                "elapsed_s": elapsed,
                "total_pending": total_pending,
                "consumers": per,
            }
            if args.prometheus:
                rec["freshness_lag_p95_ms"] = prom_instant(args.prometheus, lag_query)
                rec["parquet_rows_total"] = prom_instant(args.prometheus, rows_query)
            fh.write(json.dumps(rec) + "\n")
            fh.flush()
            if total_pending >= 0:
                samples.append(rec)
            if args.duration and elapsed >= args.duration:
                break
            # sleep in small slices so a signal stops us promptly
            slept = 0.0
            while slept < args.interval and not _stop:
                time.sleep(min(1.0, args.interval - slept))
                slept += 1.0

    # ── summary ──────────────────────────────────────────────────────────────
    valid = [s for s in samples if s["total_pending"] >= 0]
    half = valid[len(valid) // 2 :] if valid else []
    slope = _slope([s["elapsed_s"] for s in half], [s["total_pending"] for s in half])
    lag_vals = [s["freshness_lag_p95_ms"] for s in valid if s.get("freshness_lag_p95_ms") is not None]
    summary = {
        "axis": "E1-production",
        "samples": len(valid),
        "metrics": {
            "pending_max": max((s["total_pending"] for s in valid), default=None),
            "pending_last": valid[-1]["total_pending"] if valid else None,
            "pending_slope_2ndhalf_per_sec": round(slope, 4),
            "pending_stable": bool(slope <= args.pending_slope_max) if valid else None,
            "freshness_lag_p95_ms": lag_vals[-1] if lag_vals else None,
            "freshness_lag_threshold_ms": args.flush_interval_min * 60_000 + 60_000,
        },
    }
    (out / "kpi-summary.json").write_text(json.dumps(summary, indent=2))
    print(f"[kpi] summary → {out / 'kpi-summary.json'}")
    print(json.dumps(summary["metrics"], indent=2))
    return 0


if __name__ == "__main__":
    sys.exit(main())
