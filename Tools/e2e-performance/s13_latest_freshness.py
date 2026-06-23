#!/usr/bin/env python3
"""E3 — 最新値の鮮度 / stale 率 (#241).

Complements s9 (latest API p95) with the freshness KPIs: how quickly a freshly-ingested event becomes
visible via the latest API (event → Hot KV reflection), and how often a latest read is stale.

The gRPC ingress publishes to building-os.validated.telemetry, which the ingress bus mirrors into the
NATS KV hot store; the router's latest path reads the hot KV first. So per trial we:
  1. send ONE TelemetryFrame for a point with a UNIQUE value (timestamp = send time),
  2. poll GET /telemetries/query?latest=true until the returned value == the sent value (reflected),
  3. record the reflection delay (send → reflected).

  latest_freshness_p95_ms = p95 of reflection delays.
  stale_latest_ratio      = fraction of trials NOT reflected within the freshness bound (default 2000ms).

Usage:
  python s13_latest_freshness.py --out results/E3 [--trials 60] [--points 10] [--bound-ms 2000]
      [--ingress localhost:5051] [--base-url http://localhost:5000] [--oxigraph http://localhost:7878]
"""

from __future__ import annotations

import argparse
import json
import os
import sys
import time
import urllib.parse
import urllib.request
from datetime import datetime, timezone

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import s10_pointlist_integrity as s10  # noqa: E402 (reuse ingress/seed plumbing)


def latest_value(base_url: str, pid: str) -> float | None:
    url = f"{base_url.rstrip('/')}/telemetries/query?pointId={urllib.parse.quote(pid)}&latest=true"
    try:
        with urllib.request.urlopen(url, timeout=10) as r:  # noqa: S310
            doc = json.loads(r.read())
    except Exception:  # noqa: BLE001
        return None
    rows = doc.get("telemetries") if isinstance(doc, dict) else doc
    if isinstance(rows, list) and rows:
        v = rows[0].get("value")
        return float(v) if isinstance(v, (int, float)) else None
    if isinstance(doc, dict) and "value" in doc:
        v = doc["value"]
        return float(v) if isinstance(v, (int, float)) else None
    return None


def percentile(vals: list[float], q: float) -> float:
    if not vals:
        return float("nan")
    s = sorted(vals)
    return s[min(len(s) - 1, int(len(s) * q))]


def main() -> int:
    import grpc  # type: ignore

    ap = argparse.ArgumentParser(description="E3 latest freshness / stale-ratio harness (#241)")
    ap.add_argument("--out", default="results/E3")
    ap.add_argument("--trials", type=int, default=60)
    ap.add_argument("--points", type=int, default=10)
    ap.add_argument("--bound-ms", type=int, default=2000)
    ap.add_argument("--ingress", default=os.environ.get("INGRESS_TARGET", "localhost:5051"))
    ap.add_argument("--base-url", default=os.environ.get("BASE_URL", "http://localhost:5000"))
    ap.add_argument("--oxigraph", default=os.environ.get("OXIGRAPH_URL", "http://localhost:7878"))
    args = ap.parse_args()

    pb2, pb2g = s10.load_ingress_stubs()
    tag = datetime.now(timezone.utc).strftime("%Y%m%d%H%M%S")
    gw, building = f"GW-E3-{tag}", f"e3-{tag}"
    points = [f"e3pt-{tag}-{i:03d}" for i in range(args.points)]
    seeded: list[str] = []
    try:
        for p in points:
            s10.insert_point(args.oxigraph, p, gw, building); seeded.append(p)
        if not s10.wait_visible(pb2, pb2g, args.ingress, gw, points[0]):
            print("seeded points never became visible to ingress cache — aborting", file=sys.stderr)
            return 2

        delays: list[float] = []
        stale = 0
        bound_s = args.bound_ms / 1000.0
        with grpc.insecure_channel(args.ingress) as ch:
            stub = pb2g.GatewayIngressStub(ch)
            for i in range(args.trials):
                pid = points[i % len(points)]
                sent_value = 1000.0 + i  # unique per trial → identifies reflection
                t0 = time.perf_counter()
                ts = datetime.now(timezone.utc).isoformat()

                def one(p=pid, v=sent_value, t=ts):
                    yield pb2.TelemetryFrame(gateway_id=gw, point_id=p, value=v, timestamp=t)
                stub.StreamTelemetry(one(), timeout=10)

                # Poll the latest API until it reflects this event (or the bound elapses).
                reflected_ms = None
                deadline = t0 + max(bound_s, 5.0)  # poll a bit beyond the bound to record the true delay
                while time.perf_counter() < deadline:
                    if latest_value(args.base_url, pid) == sent_value:
                        reflected_ms = (time.perf_counter() - t0) * 1000.0
                        break
                    time.sleep(0.05)
                if reflected_ms is None:
                    stale += 1
                    delays.append((time.perf_counter() - t0) * 1000.0)  # cap (>= bound)
                else:
                    delays.append(reflected_ms)
                    if reflected_ms > args.bound_ms:
                        stale += 1

        p50, p95, p99 = percentile(delays, .5), percentile(delays, .95), percentile(delays, .99)
        n = args.trials
        metrics = {
            "latest_freshness_p50_ms": round(p50, 1),
            "latest_freshness_p95_ms": round(p95, 1),
            "latest_freshness_p99_ms": round(p99, 1),
            "stale_latest_ratio": round(stale / n, 5) if n else None,
        }
        result = {
            "axis": "E3_latest_value",
            "generated_at": datetime.now(timezone.utc).isoformat(),
            "config": {"trials": n, "points": args.points, "bound_ms": args.bound_ms},
            "metrics": metrics,
            "note": "event(gRPC ingress) → Hot KV → latest API 反映までの実測。latest_api_p95 は s9。",
        }
        os.makedirs(args.out, exist_ok=True)
        out_path = os.path.join(args.out, "E3-freshness.json")
        with open(out_path, "w") as f:
            json.dump(result, f, indent=2)

        def ok(name, val, pred):
            print(f"  {name:28} {val!s:10} -> {'PASS' if pred else 'FAIL'}")

        print(f"E3 freshness results (trials={n}):")
        ok("latest_freshness_p95_ms", metrics["latest_freshness_p95_ms"], p95 < 2000)
        ok("stale_latest_ratio", metrics["stale_latest_ratio"], metrics["stale_latest_ratio"] < 0.01)
        print(f"Wrote {out_path}")
        return 1 if (p95 >= 2000 or metrics["stale_latest_ratio"] >= 0.01) else 0
    finally:
        for p in seeded:
            try:
                s10.delete_point(args.oxigraph, p)
            except Exception:  # noqa: BLE001
                pass
        print(f"cleaned up {len(seeded)} seeded points")


if __name__ == "__main__":
    sys.exit(main())
