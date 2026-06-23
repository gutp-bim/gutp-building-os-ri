#!/usr/bin/env bash
set -euo pipefail

# S5 API Read Path Performance (parquet read path)
# Usage: bash Tools/e2e-performance/s5_api_read.sh [TEST_RUN_ID]
#
# Drives the unified read path (/telemetries/query → Parquet lake) with k6:
#   1. publish a short wide load so every queried point has lake data (BUILDING_ID=run isolation)
#   2. wait for the ParquetLakeWriter flush
#   3. seed those synthetic points into the OxiGraph twin (the read path 404s for unknown points)
#   4. run k6 against /telemetries/query with those point ids
#
# Environment variables:
#   BASE_URL     API base (default http://localhost:5000)
#   VUS          k6 virtual users (default 10)
#   DURATION     k6 duration (default 60s)
#   LOAD_DURATION  warm-up load seconds (default 90)
#   FLUSH_WAIT   seconds to wait for the parquet flush (default 90; needs PARQUET_FLUSH_INTERVAL=1)
#   DEVICES / POINTS_PER_DEVICE  load-gen shape (default 10 / 10, matches small scale wide)
#
# Acceptance criteria (k6 thresholds): latest p95<500ms, range p95<2000ms, errors<0.1%.

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PYTHON="${SCRIPT_DIR}/.venv/bin/python"

TEST_RUN_ID="${1:-$(date +%Y%m%dT%H%M%SZ)-s5-$$}"
BASE_URL="${BASE_URL:-http://localhost:5000}"
VUS="${VUS:-10}"
DURATION="${DURATION:-60s}"
LOAD_DURATION="${LOAD_DURATION:-90}"
FLUSH_WAIT="${FLUSH_WAIT:-90}"
DEVICES="${DEVICES:-10}"
POINTS_PER_DEVICE="${POINTS_PER_DEVICE:-10}"
OXIGRAPH_URL="${OXIGRAPH_URL:-http://localhost:7878}"

export MQTT_USERNAME="${MQTT_USERNAME:-devices}"
export MQTT_PASSWORD="${MQTT_PASSWORD:-buildingos-devices}"
export BUILDING_ID="${TEST_RUN_ID}"

echo "==> S5 API Read Path (parquet) — TEST_RUN_ID=${TEST_RUN_ID}"

# ── Verify stack ──────────────────────────────────────────────────────────────
for c in building-os.nats building-os.connector-worker building-os.minio building-os.api; do
  if ! docker ps --format "{{.Names}}" | grep -q "^${c}$"; then
    echo "ERROR: Container ${c} is not running (need the OSS stack + --profile mqtt + API)." >&2
    exit 1
  fi
done

# ── venv ──────────────────────────────────────────────────────────────────────
if [[ ! -f "${PYTHON}" ]]; then uv venv "${SCRIPT_DIR}/.venv"; fi
uv pip install -r "${SCRIPT_DIR}/requirements.txt" --python "${PYTHON}" -q

# ── Warm-up load (wide → all points carry data) ───────────────────────────────
echo "==> Starting pipeline bridge (PARQUET_MODE=true)..."
PARQUET_MODE=true "${PYTHON}" "${SCRIPT_DIR}/e2e_pipeline_bridge.py" &
BRIDGE_PID=$!
trap 'kill ${BRIDGE_PID} 2>/dev/null || true' EXIT
sleep 3

echo "==> Publishing warm-up load (wide, ${LOAD_DURATION}s, building=${BUILDING_ID})..."
"${PYTHON}" "${SCRIPT_DIR}/device_load_generator.py" \
  --scale small --profile wide --duration "${LOAD_DURATION}" --run-id "${TEST_RUN_ID}"

echo "==> Waiting ${FLUSH_WAIT}s for the ParquetLakeWriter flush..."
sleep "${FLUSH_WAIT}"

# ── Seed the synthetic points into the twin (read path 404s otherwise) ─────────
echo "==> Seeding points into the OxiGraph twin..."
POINT_IDS="$("${PYTHON}" "${SCRIPT_DIR}/seed_twin_points.py" \
  --run-id "${TEST_RUN_ID}" --devices "${DEVICES}" --points-per-device "${POINTS_PER_DEVICE}" \
  --oxigraph "${OXIGRAPH_URL}" | tail -1)"

# ── k6 ────────────────────────────────────────────────────────────────────────
RESULT_DIR="${SCRIPT_DIR}/results/${TEST_RUN_ID}"
mkdir -p "${RESULT_DIR}"
echo "==> Running k6 (VUS=${VUS}, DURATION=${DURATION}) against /telemetries/query..."
BASE_URL="${BASE_URL}" VUS="${VUS}" DURATION="${DURATION}" POINT_IDS="${POINT_IDS}" TEST_RUN_ID="${TEST_RUN_ID}" \
  k6 run --summary-export="${RESULT_DIR}/k6-summary.json" "${SCRIPT_DIR}/k6/s5_api_read.js" \
  | tee "${RESULT_DIR}/k6-output.txt"

echo "    Results: ${RESULT_DIR}/"
