#!/usr/bin/env python3
"""E4 残 KPI — 集計キャッシュヒット & multi-point スケーリング (#242).

s9 が測れない 2 つの E4 KPI を補完する:

  * agg_day_cache_hit_p95_ms : the router caches aggregate results for 5 min
    (OssTelemetryQueryRouter cacheKey). After one warming query, repeated identical agg(Day) reads are
    cache hits — we measure their p95 (KPI < 100ms).
  * multipoint_scaling       : one lake scan serves N points (GET /telemetries/cold-multi-point) vs N
    separate single-point reads. We report the NORMALISED per-point ratio
    multipoint_p95 / (K * single_p95); < 1.0 means sublinear (the shared scan is cheaper per point).

Self-contained: seeds K points, ingests frames over the last ~2h via gRPC ingress, waits a flush, then
measures. Twin points are cleaned up.

Usage:
  python s14_agg_cache_multipoint.py --out results/E4 [--points 5] [--cache-reads 40] [--mp-trials 30]
      [--flush-wait 80] [--ingress localhost:5051] [--base-url http://localhost:5000]
      [--oxigraph http://localhost:7878]
"""

from __future__ import annotations

import argparse
import json
import os
import sys
import time
import urllib.parse
import urllib.request
from datetime import datetime, timedelta, timezone

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import s10_pointlist_integrity as s10  # noqa: E402


def _get(url: str) -> float:
    """GET and return wall-clock ms (status ignored — timing only)."""
    t0 = time.perf_counter()
    try:
        with urllib.request.urlopen(url, timeout=30) as r:  # noqa: S310
            r.read()
    except Exception:  # noqa: BLE001
        pass
    return (time.perf_counter() - t0) * 1000.0


def percentile(vals: list[float], q: float) -> float:
    if not vals:
        return float("nan")
    s = sorted(vals)
    return s[min(len(s) - 1, int(len(s) * q))]


def main() -> int:
    import grpc  # type: ignore

    ap = argparse.ArgumentParser(description="E4 agg cache-hit + multipoint scaling (#242)")
    ap.add_argument("--out", default="results/E4")
    ap.add_argument("--points", type=int, default=5)
    ap.add_argument("--cache-reads", type=int, default=40)
    ap.add_argument("--mp-trials", type=int, default=30)
    ap.add_argument("--flush-wait", type=int, default=80)
    ap.add_argument("--ingress", default=os.environ.get("INGRESS_TARGET", "localhost:5051"))
    ap.add_argument("--base-url", default=os.environ.get("BASE_URL", "http://localhost:5000"))
    ap.add_argument("--oxigraph", default=os.environ.get("OXIGRAPH_URL", "http://localhost:7878"))
    args = ap.parse_args()

    pb2, pb2g = s10.load_ingress_stubs()
    tag = datetime.now(timezone.utc).strftime("%Y%m%d%H%M%S")
    gw, building = f"GW-E4-{tag}", f"e4-{tag}"
    points = [f"e4pt-{tag}-{i:03d}" for i in range(args.points)]
    enc = urllib.parse.quote
    base = args.base_url.rstrip("/")
    seeded: list[str] = []
    try:
        for p in points:
            s10.insert_point(args.oxigraph, p, gw, building); seeded.append(p)
        if not s10.wait_visible(pb2, pb2g, args.ingress, gw, points[0]):
            print("seeded points never visible — aborting", file=sys.stderr)
            return 2

        # Ingest frames into SETTLED PAST hours (−8h … −2h) so those building-hours are "ended" and the
        # CompactionWorker (run aggressive by the wrapper) produces their agg_hourly rollups. Then the
        # hour-granularity aggregate read hits pre-computed rollups (the production path) instead of the
        # variance-prone aggregate-on-read fallback over un-compacted recent data (#242 agg_hour bimodal).
        now = datetime.now(timezone.utc)
        hour0 = now.replace(minute=0, second=0, microsecond=0)
        # 6 ended hours: [-8h .. -3h], one frame every 2 min within each hour.
        frames = [(p, (hour0 - timedelta(hours=h) + timedelta(minutes=m)).isoformat())
                  for p in points for h in range(3, 9) for m in range(0, 60, 2)]

        def gen():
            for p, ts in frames:
                yield pb2.TelemetryFrame(gateway_id=gw, point_id=p, value=20.0 + (hash(ts) % 100) / 10.0, timestamp=ts)
        with grpc.insecure_channel(args.ingress) as ch:
            pb2g.GatewayIngressStub(ch).StreamTelemetry(gen(), timeout=300)
        print(f"ingested {len(frames)} frames into settled hours; waiting {args.flush_wait}s "
              f"for flush + compaction (rollups)...")
        time.sleep(args.flush_wait)

        start = enc((now - timedelta(hours=24)).strftime("%Y-%m-%dT%H:%M:%SZ"))
        end = enc(now.strftime("%Y-%m-%dT%H:%M:%SZ"))

        # ── agg_hour (cold, rollup-backed): first hour-granularity read per point (no warming) ──
        agg_hour_cold: list[float] = []
        for p in points:
            agg_hour_cold.append(_get(
                f"{base}/telemetries/query?pointId={enc(p)}&start={start}&end={end}&granularity=Hour"))
        agg_hour_p95 = percentile(agg_hour_cold, 0.95)

        # ── agg_day cache-hit: warm once per point, then measure repeated (cached) reads ──
        cache_hits: list[float] = []
        for p in points:
            agg_url = f"{base}/telemetries/query?pointId={enc(p)}&start={start}&end={end}&granularity=Day"
            _get(agg_url)  # warm (populates the 5-min router cache for this point/window)
            for _ in range(max(1, args.cache_reads // len(points))):
                cache_hits.append(_get(agg_url))
        cache_p95 = percentile(cache_hits, 0.95)

        # ── multipoint scaling: one shared lake scan (cold-multi-point, K points) vs single reads ──
        mp_q = "".join(f"&pointIds={enc(p)}" for p in points)
        mp_url = f"{base}/telemetries/cold-multi-point?startTime={start}&endTime={end}{mp_q}"
        mp_times = [_get(mp_url) for _ in range(args.mp_trials)]
        single_times = [_get(f"{base}/telemetries/query?pointId={enc(p)}&start={start}&end={end}")
                        for p in points for _ in range(max(1, args.mp_trials // len(points)))]
        mp_p95 = percentile(mp_times, 0.95)
        single_p95 = percentile(single_times, 0.95)
        scaling = (mp_p95 / (len(points) * single_p95)) if single_p95 > 0 else None

        metrics = {
            "agg_hour_cold_p95_ms": round(agg_hour_p95, 1),
            "agg_day_cache_hit_p95_ms": round(cache_p95, 1),
            "multipoint_scaling": round(scaling, 4) if scaling is not None else None,
        }
        result = {
            "axis": "E4_historical_query",
            "generated_at": datetime.now(timezone.utc).isoformat(),
            "config": {"points": args.points, "cache_reads": len(cache_hits), "mp_trials": args.mp_trials},
            "detail": {"multipoint_p95_ms": round(mp_p95, 1), "single_p95_ms": round(single_p95, 1),
                       "K": len(points)},
            "metrics": metrics,
            "note": "agg_hour_cold = rollup 済み settled hours への hour 集計の cold p95; agg cache-hit = "
                    "router 5min キャッシュの再読込 p95; multipoint_scaling = mp_p95/(K*single_p95)（<1 で sublinear）。",
        }
        os.makedirs(args.out, exist_ok=True)
        out_path = os.path.join(args.out, "E4-cache-multipoint.json")
        with open(out_path, "w") as f:
            json.dump(result, f, indent=2)

        print("E4 agg / cache-hit / multipoint results:")
        print(f"  agg_hour_cold_p95_ms      {metrics['agg_hour_cold_p95_ms']:8} -> "
              f"{'PASS' if agg_hour_p95 < 3000 else 'FAIL'} (< 3000, rollup-backed)")
        print(f"  agg_day_cache_hit_p95_ms  {metrics['agg_day_cache_hit_p95_ms']:8} -> "
              f"{'PASS' if cache_p95 < 100 else 'FAIL'}")
        print(f"  multipoint_scaling        {metrics['multipoint_scaling']!s:8} -> "
              f"{'PASS(sublinear)' if scaling is not None and scaling < 1.0 else 'FAIL'} "
              f"(mp_p95={mp_p95:.1f} single_p95={single_p95:.1f} K={len(points)})")
        print(f"Wrote {out_path}")
        return 0
    finally:
        for p in seeded:
            try:
                s10.delete_point(args.oxigraph, p)
            except Exception:  # noqa: BLE001
                pass
        print(f"cleaned up {len(seeded)} seeded points")


if __name__ == "__main__":
    sys.exit(main())
