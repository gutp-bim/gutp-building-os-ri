#!/usr/bin/env bash
# Parquet lake storage measurement (#219) — for the parquet-mode storage-efficiency and file-count KPIs.
#
# Reports:
#   - total lake bytes and object count in the `cold` bucket
#   - object count per building-hour partition (compaction KPI: should be ≤ 2, typically 1)
#   - bytes-per-row (combined with the source row count) for the ≥80% reduction vs. TimescaleDB
#     uncompressed comparison — see docs/operations/oss-warm-parquet-kpi.md for how to pair this with row counts.
#
# Lists the bucket via the S3 API directly (lake_s3_client.py, host-side — building-os.minio now runs
# RustFS (#489/#490), whose image ships no `mc` binary at all, #491). Requires the same Python
# environment (requirements.txt: boto3) as the rest of Tools/e2e-performance/.
#   MINIO_ENDPOINT_HOST (default localhost:9000), BUCKET (default cold),
#   MINIO_ROOT_USER/MINIO_ROOT_PASSWORD (or MINIO_ACCESS_KEY/MINIO_SECRET_KEY) default to the OSS
#   compose values.
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
MINIO_ENDPOINT="${MINIO_ENDPOINT_HOST:-localhost:9000}"
BUCKET="${BUCKET:-cold}"

echo "Listing lake objects via the S3 API (${MINIO_ENDPOINT}) ..."
# "<key>\t<size>" per parquet object.
LISTING="$(PYTHONPATH="$SCRIPT_DIR" python3 - "$MINIO_ENDPOINT" "$BUCKET" <<'PYEOF'
import sys
import lake_s3_client as lakes3

endpoint, bucket = sys.argv[1], sys.argv[2]
for o in lakes3.list_objects(endpoint, bucket):
    if o["key"].endswith(".parquet"):
        print(f'{o["key"]}\t{o["size"]}')
PYEOF
)"

echo
echo "=== Lake total (bucket: ${BUCKET}) ==="
TOTAL_BYTES="$(printf '%s\n' "$LISTING" | awk -F'\t' '{sum += $2} END {print sum + 0}')"
echo "${TOTAL_BYTES} bytes"

# `|| true` so an empty bucket (no matching lines) does not abort the script under `set -euo pipefail`.
PARQUET_KEYS="$(printf '%s\n' "$LISTING" | awk -F'\t' 'NF >= 2 {print $1}' || true)"

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
