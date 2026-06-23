#!/usr/bin/env bash
set -euo pipefail

# S4 Data Size And Schema Quality Test
# Usage: bash Tools/e2e-performance/s4_quality.sh [TEST_RUN_ID]
#
# Environment variables:
#   QUICK=true     — single wide run (75 pts/msg) for CI (120s each phase)
#   DURATION       — duration per phase in seconds (default: 1800; QUICK → 120)
#
# Test phases (or QUICK single wide phase):
#   Phase A: baseline profile (5 pts/msg)
#   Phase B: wide profile (75 pts/msg)
#
# Acceptance criteria:
#   - schema validation success rate = 100% (schema_invalid_count = 0)
#   - loss rate <= 1%
#   - duplicate rate <= 0.1%
#   - DB row count matches expected for each phase

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
PYTHON="${SCRIPT_DIR}/.venv/bin/python"

if [[ "${1:-}" != "" ]]; then
  TEST_RUN_ID="$1"
else
  TEST_RUN_ID="$(date +%Y%m%dT%H%M%SZ)-s4-$$"
fi

MODE="${MODE:-parquet}"
export QUALITY_MODE="${MODE}"
export MQTT_USERNAME="${MQTT_USERNAME:-devices}"
export MQTT_PASSWORD="${MQTT_PASSWORD:-buildingos-devices}"
if [[ "${MODE}" == "parquet" ]]; then SETTLE="${SETTLE:-90}"; else SETTLE="${SETTLE:-20}"; fi

echo "==> S4 Data Size And Schema Quality (mode=${MODE})"
echo "==> TEST_RUN_ID: ${TEST_RUN_ID}"

QUICK="${QUICK:-false}"
if [[ "${QUICK}" == "true" ]]; then
  SCALE="${SCALE:-small}"
  DURATION="${DURATION:-120}"
  # small scale: 10 devices, 10 pts/device (100 pts / 10 devices)
  # Phase A: baseline (5 pts/msg, effective=min(10,5)=5) → 10*(120/60)*5 = 100 rows
  BASELINE_EXPECTED="${BASELINE_EXPECTED:-100}"
  # Phase B: wide (75 pts/msg, effective=min(10,75)=10) → 10*(120/60)*10 = 200 rows
  WIDE_EXPECTED="${WIDE_EXPECTED:-200}"
else
  SCALE="${SCALE:-small}"
  DURATION="${DURATION:-1800}"
  # Phase A: baseline → 10*(1800/60)*5 = 1500 rows
  BASELINE_EXPECTED="${BASELINE_EXPECTED:-1500}"
  # Phase B: wide (effective=10) → 10*(1800/60)*10 = 3000 rows
  WIDE_EXPECTED="${WIDE_EXPECTED:-3000}"
fi

BASELINE_RUN_ID="${TEST_RUN_ID}-baseline"
WIDE_RUN_ID="${TEST_RUN_ID}-wide"

echo "    Scale: ${SCALE}, Duration: ${DURATION}s each phase"
echo "    Phase A (baseline, 5 pts/msg): expected=${BASELINE_EXPECTED}"
echo "    Phase B (wide, 75 pts/msg): expected=${WIDE_EXPECTED}"

# ── Step 1: Verify OSS stack ──────────────────────────────────────────────────
if [[ "${MODE}" == "parquet" ]]; then
  REQUIRED_CONTAINERS=("building-os.nats" "building-os.connector-worker" "building-os.minio")
else
  REQUIRED_CONTAINERS=("building-os.nats" "building-os.postgres")
fi
for c in "${REQUIRED_CONTAINERS[@]}"; do
  if ! docker ps --format "{{.Names}}" | grep -q "^${c}$"; then
    echo "ERROR: Container ${c} is not running." >&2
    echo "       Start it with: make local-up-oss" >&2
    exit 1
  fi
done

# ── Step 2: Python venv ───────────────────────────────────────────────────────
if [[ ! -f "${PYTHON}" ]]; then
  uv venv "${SCRIPT_DIR}/.venv"
fi
uv pip install -r "${SCRIPT_DIR}/requirements.txt" --python "${PYTHON}" -q

# ── Step 3: Apply schema (timescale mode only) ────────────────────────────────
if [[ "${MODE}" == "timescale" ]]; then
  docker exec building-os.postgres psql -U buildingos -d buildingos \
    -c "CREATE TABLE IF NOT EXISTS telemetry (time TIMESTAMPTZ NOT NULL, point_id TEXT NOT NULL, building TEXT, device_id TEXT NOT NULL DEFAULT '', name TEXT, value DOUBLE PRECISION, data JSONB, id TEXT);" \
    -c "SELECT create_hypertable('telemetry', 'time', if_not_exists => TRUE);" \
    -c "CREATE INDEX IF NOT EXISTS idx_telemetry_data_run_id ON telemetry USING gin (data jsonb_path_ops) WHERE data IS NOT NULL;" \
    2>/dev/null || true
fi

# ── Step 4: Start pipeline bridge ─────────────────────────────────────────────
echo "==> Starting e2e pipeline bridge (PARQUET_MODE=$([[ "${MODE}" == "parquet" ]] && echo true || echo false))..."
PARQUET_MODE="$([[ "${MODE}" == "parquet" ]] && echo true || echo false)" \
  "${PYTHON}" "${SCRIPT_DIR}/e2e_pipeline_bridge.py" &
BRIDGE_PID=$!
trap 'kill ${BRIDGE_PID} 2>/dev/null || true' EXIT
sleep 3

# ── Phase A: Baseline profile ─────────────────────────────────────────────────
echo ""
echo "==> Phase A: Baseline profile (5 pts/msg, duration=${DURATION}s)..."
BUILDING_ID="${BASELINE_RUN_ID}" "${PYTHON}" "${SCRIPT_DIR}/device_load_generator.py" \
  --scale "${SCALE}" \
  --profile baseline \
  --duration "${DURATION}" \
  --run-id "${BASELINE_RUN_ID}"

echo "==> Waiting ${SETTLE}s for Phase A writes to settle (${MODE})..."
sleep "${SETTLE}"

echo "==> Phase A quality check (expected=${BASELINE_EXPECTED})..."
"${PYTHON}" "${SCRIPT_DIR}/quality_checker.py" \
  --run-id "${BASELINE_RUN_ID}" \
  --mode "${MODE}" --building "${BASELINE_RUN_ID}" \
  --expected "${BASELINE_EXPECTED}"

BASELINE_RESULT="${SCRIPT_DIR}/results/${BASELINE_RUN_ID}/quality-check-result.json"
BASELINE_PASSED=$("${PYTHON}" -c "import json; d=json.load(open('${BASELINE_RESULT}')); print('true' if d.get('passed') else 'false')" 2>/dev/null || echo "false")

# ── Phase B: Wide profile ─────────────────────────────────────────────────────
echo ""
echo "==> Phase B: Wide profile (75 pts/msg, duration=${DURATION}s)..."
BUILDING_ID="${WIDE_RUN_ID}" "${PYTHON}" "${SCRIPT_DIR}/device_load_generator.py" \
  --scale "${SCALE}" \
  --profile wide \
  --duration "${DURATION}" \
  --run-id "${WIDE_RUN_ID}"

echo "==> Waiting ${SETTLE}s for Phase B writes to settle (${MODE})..."
sleep "${SETTLE}"

echo "==> Phase B quality check (expected=${WIDE_EXPECTED})..."
"${PYTHON}" "${SCRIPT_DIR}/quality_checker.py" \
  --run-id "${WIDE_RUN_ID}" \
  --mode "${MODE}" --building "${WIDE_RUN_ID}" \
  --expected "${WIDE_EXPECTED}"

WIDE_RESULT="${SCRIPT_DIR}/results/${WIDE_RUN_ID}/quality-check-result.json"
WIDE_PASSED=$("${PYTHON}" -c "import json; d=json.load(open('${WIDE_RESULT}')); print('true' if d.get('passed') else 'false')" 2>/dev/null || echo "false")

# ── Generate combined report ──────────────────────────────────────────────────
RESULT_DIR="${SCRIPT_DIR}/results/${TEST_RUN_ID}"
mkdir -p "${RESULT_DIR}"
REPORT_FILE="${RESULT_DIR}/report.md"

get_metric() {
  local file="$1" key="$2"
  "${PYTHON}" -c "import json; d=json.load(open('${file}')); print(d.get('${key}', 'N/A'))" 2>/dev/null || echo "N/A"
}

cat > "${REPORT_FILE}" <<EOF
# S4 Data Size And Schema Quality — ${TEST_RUN_ID}

**Date**: $(date -u +"%Y-%m-%d %H:%M:%S UTC")
**Scale**: ${SCALE}
**Duration per phase**: ${DURATION}s

## Phase A: Baseline payload (5 pts/msg)

| Metric | Value | Threshold | Status |
|--------|-------|-----------|--------|
| DB Rows | $(get_metric "${BASELINE_RESULT}" "db_row_count") | ≥ ${BASELINE_EXPECTED} | $([ "${BASELINE_PASSED}" = "true" ] && echo "✅" || echo "❌") |
| Loss Rate | $(get_metric "${BASELINE_RESULT}" "loss_rate") | ≤ 1% | - |
| Duplicate Rate | $(get_metric "${BASELINE_RESULT}" "duplicate_rate") | ≤ 0.1% | - |
| Schema Invalid | $(get_metric "${BASELINE_RESULT}" "schema_invalid_count") | = 0 | $([ "$(get_metric "${BASELINE_RESULT}" "schema_invalid_count")" = "0" ] && echo "✅" || echo "❌") |

## Phase B: Wide payload (75 pts/msg)

| Metric | Value | Threshold | Status |
|--------|-------|-----------|--------|
| DB Rows | $(get_metric "${WIDE_RESULT}" "db_row_count") | ≥ ${WIDE_EXPECTED} | $([ "${WIDE_PASSED}" = "true" ] && echo "✅" || echo "❌") |
| Loss Rate | $(get_metric "${WIDE_RESULT}" "loss_rate") | ≤ 1% | - |
| Duplicate Rate | $(get_metric "${WIDE_RESULT}" "duplicate_rate") | ≤ 0.1% | - |
| Schema Invalid | $(get_metric "${WIDE_RESULT}" "schema_invalid_count") | = 0 | $([ "$(get_metric "${WIDE_RESULT}" "schema_invalid_count")" = "0" ] && echo "✅" || echo "❌") |

## Summary

$([ "${BASELINE_PASSED}" = "true" ] && [ "${WIDE_PASSED}" = "true" ] \
  && echo "Both phases PASSED. Wide payload (75 pts/msg) schema validation matches baseline — no field mapping degradation under large payload." \
  || echo "One or more phases FAILED. Review schema_invalid_count for mapping issues.")
EOF

OVERALL_PASSED="$([ "${BASELINE_PASSED}" = "true" ] && [ "${WIDE_PASSED}" = "true" ] && echo "true" || echo "false")"

echo "    Report written: ${REPORT_FILE}"

if [[ "${OVERALL_PASSED}" == "true" ]]; then
  echo ""
  echo "✅ S4 Data Size And Schema Quality PASSED"
  exit 0
else
  echo "" >&2
  echo "❌ S4 Data Size And Schema Quality FAILED" >&2
  exit 1
fi
