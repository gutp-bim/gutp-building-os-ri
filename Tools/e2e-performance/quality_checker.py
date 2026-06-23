#!/usr/bin/env python3
"""Quality checker: validates data completeness and integrity for a given test_run_id.

Two storage backends (``--mode``):

* ``parquet`` (default) — the current OSS architecture (#216). Counts/validates rows in the
  MinIO Parquet lake via DuckDB (S3/httpfs), the same store the real ``ParquetLakeWriterWorker``
  writes to. No TimescaleDB required.
* ``timescale`` — the legacy warm path (``WARM_STORE=timescale``). Queries the TimescaleDB
  ``telemetry`` table directly.

A run is identified by the load generator's ``run_id[:8]`` prefix, which is embedded in every
``device_id`` (``perf-{type}-{run8}-{i}``) and ``point_id`` (``perf-point-{run8}-{i}-{j}``), so the
lake can be filtered without a dedicated ``test_run_id`` column.
"""

from __future__ import annotations

import argparse
import json
import logging
import os
from pathlib import Path

import requests

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
logger = logging.getLogger(__name__)

DEFAULT_DSN = os.environ.get(
    "TIMESCALE_DSN",
    "postgresql://buildingos:buildingos@localhost:5433/buildingos",
)
DEFAULT_API_BASE = os.environ.get("API_BASE_URL", "http://localhost:5000")

# MinIO / Parquet lake defaults (host-side view of the OSS compose stack).
DEFAULT_MINIO_ENDPOINT = os.environ.get("MINIO_ENDPOINT_HOST", "localhost:9000")
DEFAULT_MINIO_KEY = os.environ.get("MINIO_ACCESS_KEY", os.environ.get("MINIO_ROOT_USER", "buildingos"))
DEFAULT_MINIO_SECRET = os.environ.get("MINIO_SECRET_KEY", os.environ.get("MINIO_ROOT_PASSWORD", "buildingos123"))
DEFAULT_LAKE_BUCKET = os.environ.get("LAKE_BUCKET", "cold")


def check_lake_parquet(
    run_id: str,
    endpoint: str,
    access_key: str,
    secret_key: str,
    bucket: str,
    building: str | None = None,
) -> dict:
    """Count/validate rows in the MinIO Parquet lake via DuckDB (S3/httpfs).

    Run isolation: if ``building`` is given (the recommended path — the runner sets
    ``BUILDING_ID=<run_id>`` so each run lands in its own ``building_id=`` partition), rows are matched
    exactly by ``building`` (also enabling partition pruning). Otherwise it falls back to the
    ``run_id[:8]`` prefix embedded in device_id/point_id — which is NOT unique when run ids share an
    8-char prefix (e.g. same-day ``YYYYMMDD...`` ids), so prefer the building filter.
    Mirrors the timescale metrics shape so :func:`evaluate` is backend-agnostic.
    """
    import duckdb  # imported lazily so the timescale path needs no duckdb install

    # Hive layout: building_id={b}/year=/month=/day=/hour=/part-*.parquet (+ compact-*.parquet).
    glob = f"s3://{bucket}/**/*.parquet"

    con = duckdb.connect()
    try:
        con.execute("INSTALL httpfs; LOAD httpfs;")
        # Strip any scheme — DuckDB wants host:port in s3_endpoint.
        host = endpoint.replace("http://", "").replace("https://", "")
        con.execute(f"SET s3_endpoint='{host}';")
        con.execute("SET s3_url_style='path';")
        con.execute(f"SET s3_use_ssl={'true' if endpoint.startswith('https') else 'false'};")
        con.execute(f"SET s3_access_key_id='{access_key}';")
        con.execute(f"SET s3_secret_access_key='{secret_key}';")

        if building:
            # Exact, unique per-run match (runner sets BUILDING_ID=run_id) + partition pruning.
            src = "read_parquet(?, hive_partitioning=1, union_by_name=1) WHERE building = ?"
            params = [glob, building]
        else:
            # Fallback: run_id[:8] prefix embedded in device_id/point_id (not collision-proof).
            like = f"%{run_id[:8]}%"
            src = (
                "read_parquet(?, hive_partitioning=1, union_by_name=1) "
                "WHERE (device_id LIKE ? OR point_id LIKE ?)"
            )
            params = [glob, like, like]

        try:
            row_count = con.execute(f"SELECT count(*) FROM {src}", params).fetchone()[0]
        except duckdb.IOException:
            # No parquet objects yet (empty lake / glob matches nothing) → treat as zero rows.
            logger.warning("Parquet lake glob matched no objects (%s); row_count=0", glob)
            return {
                "db_row_count": 0,
                "duplicate_count": 0,
                "schema_valid_count": 0,
                "schema_invalid_count": 0,
            }

        # Same semantics as the timescale check: number of (point_id, time) groups with > 1 row.
        duplicate_count = con.execute(
            f"SELECT count(*) FROM ("
            f"  SELECT point_id, time, count(*) AS cnt FROM {src}"
            f"  GROUP BY point_id, time HAVING count(*) > 1)",
            params,
        ).fetchone()[0]

        validity = con.execute(
            f"SELECT "
            f"  count(*) FILTER (WHERE point_id IS NOT NULL AND time IS NOT NULL AND value IS NOT NULL),"
            f"  count(*) FILTER (WHERE point_id IS NULL OR time IS NULL OR value IS NULL) "
            f"FROM {src}",
            params,
        ).fetchone()
    finally:
        con.close()

    return {
        "db_row_count": int(row_count),
        "duplicate_count": int(duplicate_count),
        "schema_valid_count": int(validity[0]),
        "schema_invalid_count": int(validity[1]),
    }


def check_db(run_id: str, dsn: str) -> dict:
    """Query TimescaleDB for rows matching test_run_id and compute quality metrics."""
    import psycopg2
    import psycopg2.extras

    conn = psycopg2.connect(dsn)
    try:
        with conn.cursor(cursor_factory=psycopg2.extras.DictCursor) as cur:
            # Total row count for this run
            cur.execute(
                "SELECT COUNT(*) FROM telemetry WHERE data->>'test_run_id' = %s",
                (run_id,),
            )
            row_count: int = cur.fetchone()[0]

            # Duplicate check: same device_id + point_id + datetime
            cur.execute(
                """
                SELECT COUNT(*) FROM (
                    SELECT device_id, point_id, time, COUNT(*) AS cnt
                    FROM telemetry
                    WHERE data->>'test_run_id' = %s
                    GROUP BY device_id, point_id, time
                    HAVING COUNT(*) > 1
                ) dups
                """,
                (run_id,),
            )
            duplicate_count: int = cur.fetchone()[0]

            # Schema validity: rows with non-null device_id, point_id, time and non-null value
            cur.execute(
                """
                SELECT
                    COUNT(*) FILTER (WHERE device_id IS NOT NULL
                                       AND point_id  IS NOT NULL
                                       AND time      IS NOT NULL
                                       AND value     IS NOT NULL) AS valid_cnt,
                    COUNT(*) FILTER (WHERE device_id IS NULL
                                        OR point_id  IS NULL
                                        OR time      IS NULL
                                        OR value     IS NULL) AS invalid_cnt
                FROM telemetry
                WHERE data->>'test_run_id' = %s
                """,
                (run_id,),
            )
            validity = cur.fetchone()
            schema_valid_count: int = validity[0]
            schema_invalid_count: int = validity[1]
    finally:
        conn.close()

    return {
        "db_row_count": row_count,
        "duplicate_count": duplicate_count,
        "schema_valid_count": schema_valid_count,
        "schema_invalid_count": schema_invalid_count,
    }


def check_api(run_id: str, api_base: str) -> int:
    """Fetch row count from API server for the given test_run_id. Returns -1 on failure."""
    url = f"{api_base}/api/telemetry/search"
    params = {"test_run_id": run_id, "limit": 1}
    try:
        resp = requests.get(url, params=params, timeout=30)
        if resp.status_code == 200:
            body = resp.json()
            # Try common response shapes
            if isinstance(body, list):
                return len(body)
            if isinstance(body, dict):
                if "total" in body:
                    return int(body["total"])
                if "count" in body:
                    return int(body["count"])
                if "items" in body and isinstance(body["items"], list):
                    return len(body["items"])
        logger.warning("API returned status %d for test_run_id=%s", resp.status_code, run_id)
        return -1
    except requests.RequestException as exc:
        logger.warning("API check failed: %s", exc)
        return -1


def evaluate(
    run_id: str,
    expected: int,
    db_metrics: dict,
    api_row_count: int,
) -> dict:
    db_count = db_metrics["db_row_count"]
    duplicate_count = db_metrics["duplicate_count"]
    schema_invalid = db_metrics["schema_invalid_count"]

    loss_rate = 0.0 if expected == 0 else max(0.0, (expected - db_count) / expected)
    duplicate_rate = 0.0 if db_count == 0 else duplicate_count / db_count

    # Pass criteria
    passed = (
        loss_rate <= 0.01          # <= 1% loss
        and duplicate_rate <= 0.001  # <= 0.1% duplicates
        and schema_invalid == 0
        and db_count > 0
    )

    return {
        "test_run_id": run_id,
        "db_row_count": db_count,
        "api_row_count": api_row_count,
        "duplicate_count": duplicate_count,
        "loss_rate": round(loss_rate, 6),
        "duplicate_rate": round(duplicate_rate, 6),
        "schema_valid_count": db_metrics["schema_valid_count"],
        "schema_invalid_count": schema_invalid,
        "passed": passed,
    }


def main() -> None:
    parser = argparse.ArgumentParser(description="Quality checker for Building OS E2E performance tests")
    parser.add_argument("--run-id",   required=True, help="test_run_id to evaluate")
    parser.add_argument("--expected", type=int, required=True, help="Expected total message count")
    parser.add_argument("--mode", choices=["parquet", "timescale"],
                        default=os.environ.get("QUALITY_MODE", "parquet"),
                        help="Storage backend to verify (default: parquet)")
    parser.add_argument("--dsn",      default=DEFAULT_DSN, help="TimescaleDB DSN (timescale mode)")
    parser.add_argument("--minio-endpoint", default=DEFAULT_MINIO_ENDPOINT, help="MinIO endpoint host:port (parquet mode)")
    parser.add_argument("--minio-key",      default=DEFAULT_MINIO_KEY, help="MinIO access key (parquet mode)")
    parser.add_argument("--minio-secret",   default=DEFAULT_MINIO_SECRET, help="MinIO secret key (parquet mode)")
    parser.add_argument("--bucket",         default=DEFAULT_LAKE_BUCKET, help="Parquet lake bucket (parquet mode)")
    parser.add_argument("--building",       default=os.environ.get("BUILDING_ID"),
                        help="Building partition to match exactly (parquet mode; runner sets BUILDING_ID=run_id)")
    parser.add_argument("--api-base", default=DEFAULT_API_BASE, help="API Server base URL")
    args = parser.parse_args()

    if args.mode == "parquet":
        logger.info("Checking Parquet lake (bucket=%s, building=%s) for run_id=%s (expected=%d)",
                    args.bucket, args.building or "<run8 prefix>", args.run_id, args.expected)
        db_metrics = check_lake_parquet(
            args.run_id, args.minio_endpoint, args.minio_key, args.minio_secret, args.bucket,
            building=args.building or None)
    else:
        logger.info("Checking TimescaleDB for run_id=%s (expected=%d)", args.run_id, args.expected)
        db_metrics = check_db(args.run_id, args.dsn)
    logger.info("Storage metrics (%s): %s", args.mode, db_metrics)

    logger.info("Checking API for run_id=%s", args.run_id)
    api_row_count = check_api(args.run_id, args.api_base)
    logger.info("API row count: %d", api_row_count)

    result = evaluate(args.run_id, args.expected, db_metrics, api_row_count)
    result["mode"] = args.mode

    out_dir = Path(__file__).parent / "results" / args.run_id
    out_dir.mkdir(parents=True, exist_ok=True)
    out_path = out_dir / "quality-check-result.json"
    out_path.write_text(json.dumps(result, indent=2))
    logger.info("Result saved to %s", out_path)

    status = "PASSED" if result["passed"] else "FAILED"
    logger.info(
        "Quality check %s — db=%d api=%d loss=%.4f%% dups=%d schema_invalid=%d",
        status,
        result["db_row_count"],
        result["api_row_count"],
        result["loss_rate"] * 100,
        result["duplicate_count"],
        result["schema_invalid_count"],
    )


if __name__ == "__main__":
    main()
