#!/usr/bin/env python3
"""
DuckDB ad-hoc query runner for MinIO Parquet cold storage.
Replaces Microsoft Fabric Lakehouse for small/medium analytics.

Environment variables:
    MINIO_ENDPOINT    MinIO endpoint (default: localhost:9000)
    MINIO_ACCESS_KEY  MinIO access key (default: minio)
    MINIO_SECRET_KEY  MinIO secret key (default: minio123)
    MINIO_BUCKET      MinIO bucket name (default: building-os-cold)
    MINIO_USE_SSL     "1" for HTTPS (default: 0)
    PARQUET_PREFIX    Object prefix for telemetry Parquet files (default: telemetry)
    DUCKDB_DB         Path to DuckDB database file (default: :memory:)

Usage:
    python query.py --sql "SELECT * FROM telemetry LIMIT 10"
    python query.py --building ENG2 --from 2024-01-01 --to 2024-06-30
    python query.py --daily-summary --building ENG2
"""

from __future__ import annotations

import argparse
import os
import sys
from typing import Optional

import duckdb


def _get_s3_config() -> dict:
    return {
        "endpoint": os.environ.get("MINIO_ENDPOINT", "localhost:9000"),
        "access_key": os.environ.get("MINIO_ACCESS_KEY", "minio"),
        "secret_key": os.environ.get("MINIO_SECRET_KEY", "minio123"),
        "bucket": os.environ.get("MINIO_BUCKET", "building-os-cold"),
        "use_ssl": os.environ.get("MINIO_USE_SSL", "0") == "1",
        "prefix": os.environ.get("PARQUET_PREFIX", "telemetry"),
    }


def build_connection(db_path: str = ":memory:") -> duckdb.DuckDBPyConnection:
    """Create a DuckDB connection configured for MinIO S3."""
    cfg = _get_s3_config()
    con = duckdb.connect(db_path)

    # Configure S3-compatible MinIO endpoint
    con.execute("INSTALL httpfs; LOAD httpfs;")
    con.execute(f"SET s3_endpoint='{cfg['endpoint']}';")
    con.execute(f"SET s3_access_key_id='{cfg['access_key']}';")
    con.execute(f"SET s3_secret_access_key='{cfg['secret_key']}';")
    con.execute(f"SET s3_use_ssl={'true' if cfg['use_ssl'] else 'false'};")
    con.execute("SET s3_url_style='path';")  # MinIO requires path-style

    # Register telemetry view over all Parquet files in the bucket
    bucket = cfg["bucket"]
    prefix = cfg["prefix"]
    con.execute(f"""
        CREATE OR REPLACE VIEW telemetry AS
        SELECT * FROM read_parquet('s3://{bucket}/{prefix}/**/*.parquet', hive_partitioning=true)
    """)

    return con


def run_query(con: duckdb.DuckDBPyConnection, sql: str) -> duckdb.DuckDBPyRelation:
    return con.execute(sql)


# ── Canned queries (ported from Fabric notebooks) ────────────────────────────

DAILY_SUMMARY_SQL = """
SELECT
    strftime(time, '%Y-%m-%d') AS date,
    building,
    point_id,
    COUNT(*)                    AS sample_count,
    AVG(value)                  AS avg_value,
    MIN(value)                  AS min_value,
    MAX(value)                  AS max_value
FROM telemetry
WHERE building = $building
  AND time >= $from_ts::TIMESTAMPTZ
  AND time <  $to_ts::TIMESTAMPTZ
GROUP BY 1, 2, 3
ORDER BY 1, 2, 3
"""

HOURLY_SUMMARY_SQL = """
SELECT
    date_trunc('hour', time)    AS hour,
    building,
    point_id,
    COUNT(*)                    AS sample_count,
    AVG(value)                  AS avg_value
FROM telemetry
WHERE building = $building
  AND time >= $from_ts::TIMESTAMPTZ
  AND time <  $to_ts::TIMESTAMPTZ
GROUP BY 1, 2, 3
ORDER BY 1, 2, 3
"""

LATEST_PER_POINT_SQL = """
SELECT DISTINCT ON (point_id)
    point_id, building, device_id, name, value, time
FROM telemetry
WHERE building = $building
ORDER BY point_id, time DESC
"""


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(description="DuckDB MinIO Parquet query runner")
    parser.add_argument("--sql", help="Raw SQL to execute")
    parser.add_argument("--daily-summary", action="store_true")
    parser.add_argument("--hourly-summary", action="store_true")
    parser.add_argument("--latest", action="store_true", help="Latest value per point")
    parser.add_argument("--building", default="ENG2")
    parser.add_argument("--from", dest="from_ts", default="2024-01-01")
    parser.add_argument("--to", dest="to_ts", default="2030-01-01")
    parser.add_argument("--db", default=":memory:", help="DuckDB database file path")
    parser.add_argument("--output", choices=["table", "csv", "json"], default="table")
    args = parser.parse_args(argv)

    con = build_connection(args.db)

    if args.sql:
        result = run_query(con, args.sql)
    elif args.daily_summary:
        result = con.execute(DAILY_SUMMARY_SQL, {
            "building": args.building,
            "from_ts": args.from_ts,
            "to_ts": args.to_ts,
        })
    elif args.hourly_summary:
        result = con.execute(HOURLY_SUMMARY_SQL, {
            "building": args.building,
            "from_ts": args.from_ts,
            "to_ts": args.to_ts,
        })
    elif args.latest:
        result = con.execute(LATEST_PER_POINT_SQL, {"building": args.building})
    else:
        parser.print_help()
        return 1

    df = result.df()
    if args.output == "csv":
        print(df.to_csv(index=False))
    elif args.output == "json":
        print(df.to_json(orient="records", indent=2))
    else:
        print(df.to_string(index=False))

    return 0


if __name__ == "__main__":
    sys.exit(main())
