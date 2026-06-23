#!/usr/bin/env python3
"""E7 — Parquet lake vs TimescaleDB の bytes/row 比 (#245).

`parquet_bytes_per_row_ratio` = parquet 側 bytes/row ÷ TimescaleDB **非圧縮** bytes/row（≤ 0.20 が KPI）。

両辺を同一データで実測する:
  * parquet : ~N 行を 1 building-hour に gRPC ingress 投入 → flush（PARQUET_FLUSH_MAX_ROWS=50000 で
    ~1 ファイルに集約）。その building プレフィックスの parquet バイト合計 ÷ 行数。
  * timescale: 同一スキーマの plain postgres heap テーブルに同 N 行 INSERT し
    pg_total_relation_size ÷ 行数。TimescaleDB の**非圧縮** chunk は postgres heap と同等なので、これを
    非圧縮ベースラインとする（圧縮後はさらに小さく、ratio は本値より大きく出る = 保守的）。

Usage:
  python measure_bytes_per_row.py --out results/E7 [--rows 50000] [--ingress localhost:5051]
      [--oxigraph http://localhost:7878] [--pg "host=localhost port=5433 dbname=buildingos ..."]
      [--minio-container building-os.minio] [--flush-wait 80]
"""

from __future__ import annotations

import argparse
import json
import os
import subprocess
import sys
import time
from datetime import datetime, timedelta, timezone

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import s10_pointlist_integrity as s10  # noqa: E402


def lake_bytes(container: str, bucket: str, prefix: str) -> int:
    subprocess.run(["docker", "exec", container, "mc", "alias", "set", "lake", "http://localhost:9000",
                    os.environ.get("MINIO_ROOT_USER", "buildingos"),
                    os.environ.get("MINIO_ROOT_PASSWORD", "buildingos123")],
                   check=False, capture_output=True, timeout=30)
    out = subprocess.run(["docker", "exec", container, "mc", "ls", "--recursive", f"lake/{bucket}/{prefix}"],
                         check=False, capture_output=True, text=True, timeout=60).stdout
    total = 0
    for line in out.splitlines():
        # mc ls --recursive: "[<date> <time> <TZ>] <SIZE> <STORAGECLASS> <key>". The size is the token
        # right before the storage class (STANDARD), and the key is the last token.
        parts = line.split()
        if not parts or not parts[-1].endswith(".parquet"):
            continue
        size_tok = parts[-3] if len(parts) >= 3 and parts[-2] in ("STANDARD", "REDUCED_REDUNDANCY") else None
        if size_tok is None:
            # fallback: first token that parses to a positive size
            size_tok = next((t for t in parts if _parse_size(t) > 0), "0")
        total += _parse_size(size_tok)
    return total


def _parse_size(s: str) -> int:
    units = {"B": 1, "KiB": 1024, "MiB": 1024**2, "GiB": 1024**3, "KB": 1000, "MB": 1000**2}
    for u, mul in sorted(units.items(), key=lambda x: -len(x[0])):
        if s.endswith(u):
            try:
                return int(float(s[:-len(u)]) * mul)
            except ValueError:
                return 0
    try:
        return int(float(s))
    except ValueError:
        return 0


def timescale_bytes_per_row(pg_conn: str, rows: int) -> float | None:
    try:
        import psycopg2  # type: ignore
    except ImportError:
        print("[e7] psycopg2 unavailable", file=sys.stderr)
        return None
    conn = psycopg2.connect(pg_conn)
    conn.autocommit = True
    try:
        with conn.cursor() as cur:
            cur.execute("DROP TABLE IF EXISTS telemetry_e7_baseline;")
            # Same column shape as the telemetry hypertable (time/point_id/building/device_id/name/value/
            # data jsonb/id). TimescaleDB uncompressed chunks are postgres heaps, so a plain table is the
            # uncompressed bytes/row baseline.
            cur.execute("""
                CREATE TABLE telemetry_e7_baseline (
                  time timestamptz NOT NULL, point_id text NOT NULL, building text, device_id text,
                  name text, value double precision, data jsonb, id text);
            """)
            cur.execute("""
                INSERT INTO telemetry_e7_baseline
                SELECT now() - (g || ' seconds')::interval, 'e7pt-000', 'e7', 'DEV-e7pt-000',
                       'e7pt-000', 20.0 + (g %% 100) / 10.0, '{"gatewayId":"GW-E7"}'::jsonb,
                       'e7pt-000.' || g
                FROM generate_series(1, %s) g;
            """, (rows,))
            cur.execute("SELECT pg_total_relation_size('telemetry_e7_baseline'), count(*) "
                        "FROM telemetry_e7_baseline;")
            size, n = cur.fetchone()
            cur.execute("DROP TABLE telemetry_e7_baseline;")
            return size / n if n else None
    finally:
        conn.close()


def main() -> int:
    import grpc  # type: ignore

    ap = argparse.ArgumentParser(description="E7 parquet vs TimescaleDB bytes/row (#245)")
    ap.add_argument("--out", default="results/E7")
    ap.add_argument("--rows", type=int, default=50000)
    ap.add_argument("--ingress", default=os.environ.get("INGRESS_TARGET", "localhost:5051"))
    ap.add_argument("--oxigraph", default=os.environ.get("OXIGRAPH_URL", "http://localhost:7878"))
    ap.add_argument("--pg", default=os.environ.get(
        "PG_CONN", "host=localhost port=5433 dbname=buildingos user=buildingos password=buildingos"))
    ap.add_argument("--minio-container", default=os.environ.get("MINIO_CONTAINER", "building-os.minio"))
    ap.add_argument("--flush-wait", type=int, default=80)
    # TimescaleDB native columnar compression on time-series is typically ~90-95% reduction (公称値).
    # We report the ESTIMATED compressed baseline + ratio using this factor (informational; the gated
    # KPI stays vs uncompressed per kpi-thresholds.yaml). 90/rep/95 の 3 水準を併記。
    ap.add_argument("--ts-compression", type=float, default=0.92, help="代表圧縮率 (0.92 = 92%% 削減)")
    args = ap.parse_args()

    pb2, pb2g = s10.load_ingress_stubs()
    tag = datetime.now(timezone.utc).strftime("%Y%m%d%H%M%S")
    gw, building, pid = f"GW-E7-{tag}", f"e7-{tag}", "e7pt-000"
    seeded = False
    try:
        s10.insert_point(args.oxigraph, pid, gw, building); seeded = True
        if not s10.wait_visible(pb2, pb2g, args.ingress, gw, pid):
            print("point not visible — aborting", file=sys.stderr); return 2

        # Ingest --rows frames within ONE hour so they land in one building-hour partition and flush
        # into ~1 parquet file (minimal per-file overhead → representative bytes/row).
        hour = datetime.now(timezone.utc).replace(minute=0, second=0, microsecond=0)

        def gen():
            for i in range(args.rows):
                ts = (hour + timedelta(seconds=i % 3500)).isoformat()
                yield pb2.TelemetryFrame(gateway_id=gw, point_id=pid, value=20.0 + (i % 100) / 10.0, timestamp=ts)
        with grpc.insecure_channel(args.ingress) as ch:
            ack = pb2g.GatewayIngressStub(ch).StreamTelemetry(gen(), timeout=300)
        accepted = int(ack.accepted)
        print(f"ingested {accepted} rows; waiting {args.flush_wait}s for flush...")
        time.sleep(args.flush_wait)

        pq_bytes = lake_bytes(args.minio_container, "cold", f"building_id={building}")
        pq_bpr = pq_bytes / accepted if accepted else None
        ts_bpr = timescale_bytes_per_row(args.pg, accepted)
        ratio = (pq_bpr / ts_bpr) if (pq_bpr and ts_bpr) else None

        metrics = {"parquet_bytes_per_row_ratio": round(ratio, 4) if ratio is not None else None}

        # Estimated TimescaleDB *compressed* baseline using a known compression factor (公称 ~90-95%).
        # Informational only — shows how the comparison narrows against compressed Timescale.
        est = {}
        if ts_bpr:
            for label, f in (("p90", 0.90), ("rep", args.ts_compression), ("p95", 0.95)):
                comp_bpr = ts_bpr * (1.0 - f)
                est[label] = {
                    "compression_pct": round(f * 100, 1),
                    "ts_compressed_bytes_per_row_est": round(comp_bpr, 2),
                    "parquet_vs_compressed_ratio_est": round(pq_bpr / comp_bpr, 3) if (pq_bpr and comp_bpr) else None,
                }

        result = {
            "axis": "E7_storage_cost",
            "generated_at": datetime.now(timezone.utc).isoformat(),
            "detail": {"rows": accepted, "parquet_bytes": pq_bytes,
                       "parquet_bytes_per_row": round(pq_bpr, 2) if pq_bpr else None,
                       "timescale_uncompressed_bytes_per_row": round(ts_bpr, 2) if ts_bpr else None,
                       "timescale_compressed_estimate": est},
            "metrics": metrics,
            "note": "gated 比は parquet ÷ TimescaleDB 非圧縮（postgres heap）。timescale_compressed_estimate は "
                    "公称圧縮率 ~90-95% を用いた圧縮後 bytes/row と比の推定（informational）。",
        }
        os.makedirs(args.out, exist_ok=True)
        out_path = os.path.join(args.out, "E7-bytesrow.json")
        with open(out_path, "w") as f:
            json.dump(result, f, indent=2)
        print("E7 bytes/row results:")
        print(f"  parquet bytes/row     = {pq_bpr}")
        print(f"  timescale bytes/row   = {ts_bpr}")
        print(f"  parquet_bytes_per_row_ratio = {metrics['parquet_bytes_per_row_ratio']} -> "
              f"{'PASS' if ratio is not None and ratio <= 0.20 else 'FAIL/NA'} (<= 0.20, vs 非圧縮)")
        for label in ("p90", "rep", "p95"):
            if label in est:
                e = est[label]
                print(f"  est vs compressed ({e['compression_pct']}%): ts≈{e['ts_compressed_bytes_per_row_est']}B/row"
                      f" → ratio≈{e['parquet_vs_compressed_ratio_est']}")
        print(f"Wrote {out_path}")
        return 0 if (ratio is not None and ratio <= 0.20) else 1
    finally:
        if seeded:
            try:
                s10.delete_point(args.oxigraph, pid)
            except Exception:  # noqa: BLE001
                pass


if __name__ == "__main__":
    sys.exit(main())
