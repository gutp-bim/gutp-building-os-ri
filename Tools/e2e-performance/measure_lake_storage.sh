#!/usr/bin/env bash
# Parquet lake storage measurement (#219) — for the parquet-mode storage-efficiency and file-count KPIs.
#
# Reports:
#   - total lake bytes and object count in the `cold` bucket
#   - object count per building-hour partition (compaction KPI: should be ≤ 2, typically 1)
#   - bytes-per-row (combined with the source row count) for the ≥80% reduction vs. TimescaleDB
#     uncompressed comparison — see docs/oss-warm-parquet-kpi.md for how to pair this with row counts.
#
# Uses the MinIO client (`mc`) inside the MinIO container so no host tooling is needed.
#   MINIO_CONTAINER (default building-os.minio), BUCKET (default cold),
#   MINIO_ALIAS host/creds default to the OSS compose values.
set -euo pipefail

MINIO_CONTAINER="${MINIO_CONTAINER:-building-os.minio}"
BUCKET="${BUCKET:-cold}"
MINIO_URL="${MINIO_URL:-http://localhost:9000}"
MINIO_KEY="${MINIO_ROOT_USER:-buildingos}"
MINIO_SECRET="${MINIO_ROOT_PASSWORD:-buildingos123}"

mc() { docker exec -i "$MINIO_CONTAINER" mc "$@"; }

echo "Configuring mc alias inside ${MINIO_CONTAINER} ..."
mc alias set lake "$MINIO_URL" "$MINIO_KEY" "$MINIO_SECRET" >/dev/null

echo
echo "=== Lake total (bucket: ${BUCKET}) ==="
mc du "lake/${BUCKET}"

# Collect the parquet keys once. `|| true` so an empty bucket (0 matches → grep exit 1) does not
# abort the script under `set -euo pipefail`.
PARQUET_KEYS="$(mc ls --recursive "lake/${BUCKET}" | awk '{print $NF}' | grep '\.parquet$' || true)"

echo
echo "=== Object count ==="
TOTAL_OBJECTS="$([ -z "$PARQUET_KEYS" ] && echo 0 || printf '%s\n' "$PARQUET_KEYS" | grep -c .)"
echo "parquet objects: ${TOTAL_OBJECTS}"

echo
echo "=== Objects per building-hour partition (compaction KPI: ≤ 2) ==="
if [ -z "$PARQUET_KEYS" ]; then
  echo "(no parquet objects)"
else
  # Strip the trailing /<file>.parquet to get the hour-partition dir, then count files per dir.
  printf '%s\n' "$PARQUET_KEYS" \
    | sed -E 's#/[^/]+\.parquet$#/#' | sort | uniq -c | sort -rn | head -20
fi

echo
echo "=== Max objects in any single building-hour ==="
if [ -z "$PARQUET_KEYS" ]; then
  echo "max objects/partition = 0"
else
  printf '%s\n' "$PARQUET_KEYS" \
    | sed -E 's#/[^/]+\.parquet$#/#' | sort | uniq -c | sort -rn | head -1 \
    | awk '{print "max objects/partition = "$1" ("$2")"}'
fi

echo
echo "To compute bytes/row: divide the total lake bytes above by the row count for the same range"
echo "(SELECT count(*) FROM telemetry WHERE time >= ... ), and compare to TimescaleDB uncompressed"
echo "bytes/row. Target: parquet bytes/row ≤ 20% of timescale uncompressed (≥80% reduction)."
