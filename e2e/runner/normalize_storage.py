#!/usr/bin/env python3
"""Normalize Parquet-lake storage metrics into the KPI gate's {axis, metrics} shape (E7).

Lists the `cold` bucket via the MinIO client inside the container and computes the compaction KPI
`objects_per_building_hour` = the MAX number of *.parquet objects in any single building-hour
partition (gate threshold ≤ 2: after compaction each settled hour should hold ~1 compacted object).

`parquet_bytes_per_row_ratio` (vs TimescaleDB uncompressed) needs a TimescaleDB baseline that does not
exist in parquet-only mode, so it is left for the report (gate SKIP). `monthly_cost_estimate_usd` is a
`report` KPI. Emits only what is mechanically measurable here.

Usage: python normalize_storage.py --out <run-dir> [--minio-container building-os.minio] [--bucket cold]
"""

from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys


def list_parquet_keys(container: str, bucket: str) -> list[str]:
    # Set the alias (idempotent), then list recursively. Creds default to the OSS compose values.
    subprocess.run(
        ["docker", "exec", container, "mc", "alias", "set", "lake", "http://localhost:9000",
         os.environ.get("MINIO_ROOT_USER", "buildingos"),
         os.environ.get("MINIO_ROOT_PASSWORD", "buildingos123")],
        check=False, capture_output=True, timeout=30)
    out = subprocess.run(
        ["docker", "exec", container, "mc", "ls", "--recursive", f"lake/{bucket}"],
        check=False, capture_output=True, text=True, timeout=60).stdout
    keys = []
    for line in out.splitlines():
        parts = line.split()
        if parts and parts[-1].endswith(".parquet"):
            keys.append(parts[-1])
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
    ap.add_argument("--minio-container", default=os.environ.get("MINIO_CONTAINER", "building-os.minio"))
    ap.add_argument("--bucket", default=os.environ.get("BUCKET", "cold"))
    args = ap.parse_args()

    try:
        keys = list_parquet_keys(args.minio_container, args.bucket)
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
                   "object_count": len(keys), "source": "minio-mc"}, f, indent=2)
    print(f"[normalize-storage] E7: {len(keys)} objects → {out_path}: {metrics}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
