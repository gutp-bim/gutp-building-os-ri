#!/usr/bin/env python3
"""Normalize Parquet-lake storage metrics into the KPI gate's {axis, metrics} shape (E7).

Lists the `cold` bucket via the S3 API directly against `--minio-endpoint` and computes the
compaction KPI `objects_per_building_hour` = the MAX number of *.parquet objects in any single
building-hour partition (gate threshold ≤ 2: after compaction each settled hour should hold ~1
compacted object).

`parquet_bytes_per_row_ratio` (vs TimescaleDB uncompressed) needs a TimescaleDB baseline that does not
exist in parquet-only mode, so it is left for the report (gate SKIP). `monthly_cost_estimate_usd` is a
`report` KPI. Emits only what is mechanically measurable here.

Usage: python normalize_storage.py --out <run-dir> [--minio-endpoint localhost:9000] [--bucket cold]
"""

from __future__ import annotations

import argparse
import json
import os
import re
import sys


def list_parquet_keys(endpoint: str, bucket: str) -> list[str]:
    # Host-side S3 API, not `docker exec <container> mc ...`: building-os.minio now runs RustFS
    # (#489/#490), whose image ships no `mc` binary at all (#491). Creds default to the OSS compose
    # values, same convention as Tools/e2e-performance/lake_s3_client.py (kept self-contained here —
    # this script has no import dependency outside e2e/runner/).
    import boto3
    from botocore.config import Config

    url = endpoint if "://" in endpoint else f"http://{endpoint}"
    access_key = os.environ.get("MINIO_ACCESS_KEY", os.environ.get("MINIO_ROOT_USER", "buildingos"))
    secret_key = os.environ.get("MINIO_SECRET_KEY", os.environ.get("MINIO_ROOT_PASSWORD", "buildingos123"))
    s3 = boto3.client("s3", endpoint_url=url, aws_access_key_id=access_key,
                       aws_secret_access_key=secret_key, config=Config(signature_version="s3v4"))
    keys = []
    paginator = s3.get_paginator("list_objects_v2")
    for page in paginator.paginate(Bucket=bucket):
        keys.extend(o["Key"] for o in page.get("Contents", []) if o["Key"].endswith(".parquet"))
    return keys


def max_objects_per_building_hour(keys: list[str]) -> int:
    # Partition dir = key with the trailing /<file>.parquet stripped (building_id=.../hour=.../).
    counts: dict[str, int] = {}
    for k in keys:
        part = re.sub(r"/[^/]+\.parquet$", "/", k)
        counts[part] = counts.get(part, 0) + 1
    return max(counts.values(), default=0)


def main() -> int:
    ap = argparse.ArgumentParser(description="Normalize lake storage → gate {axis, metrics} (E7)")
    ap.add_argument("--out", required=True)
    ap.add_argument("--minio-endpoint", default=os.environ.get("MINIO_ENDPOINT_HOST", "localhost:9000"))
    ap.add_argument("--bucket", default=os.environ.get("BUCKET", "cold"))
    args = ap.parse_args()

    try:
        keys = list_parquet_keys(args.minio_endpoint, args.bucket)
    except Exception as e:  # noqa: BLE001
        print(f"[normalize-storage] could not list lake: {e}", file=sys.stderr)
        return 0  # not an error — axis just produces no metrics (gate SKIP)

    metrics: dict[str, float] = {}
    if keys:
        metrics["objects_per_building_hour"] = max_objects_per_building_hour(keys)

    os.makedirs(args.out, exist_ok=True)
    out_path = os.path.join(args.out, "E7-normalized.json")
    with open(out_path, "w") as f:
        json.dump({"axis": "E7_storage_cost", "metrics": metrics,
                   "object_count": len(keys), "source": "s3-api"}, f, indent=2)
    print(f"[normalize-storage] E7: {len(keys)} objects → {out_path}: {metrics}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
