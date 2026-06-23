#!/usr/bin/env bash
set -euo pipefail

# S3 Burst And Backpressure Test
# Usage: bash Tools/e2e-performance/s3_burst.sh [TEST_RUN_ID]
#
# Environment variables:
#   QUICK=true          — small scale / shortened phases for CI
#   BURST_DURATION      — burst phase duration in seconds (default: 900; QUICK → 120)
#   RECOVERY_DURATION   — recovery phase duration in seconds (default: 900; QUICK → 120)
#
# Test phases:
#   Phase 1: medium scale, burst profile (10s interval) — simulates 5x traffic spike
#   Phase 2: medium scale, baseline profile (60s interval) — recovery verification
#
# Acceptance criteria:
#   - Both phases: loss rate <= 1%, duplicate rate <= 0.1%, schema invalid = 0
#   - Recovery phase passes quality check (NATS backpressure cleared)

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
PYTHON="${SCRIPT_DIR}/.venv/bin/python"

if [[ "${1:-}" != "" ]]; then
  TEST_RUN_ID="$1"
else
  TEST_RUN_ID="$(date +%Y%m%dT%H%M%SZ)-s3-$$"
fi

MODE="${MODE:-parquet}"
export QUALITY_MODE="${MODE}"
# Mosquitto enforces auth (allow_anonymous false); default to compose dev creds.
export MQTT_USERNAME="${MQTT_USERNAME:-devices}"
export MQTT_PASSWORD="${MQTT_PASSWORD:-buildingos-devices}"
# Settle wait per phase: parquet must exceed the writer PARQUET_FLUSH_INTERVAL (set =1 for QUICK).
if [[ "${MODE}" == "parquet" ]]; then SETTLE="${SETTLE:-90}"; else SETTLE="${SETTLE:-25}"; fi

echo "==> S3 Burst And Backpressure (mode=${MODE})"
echo "==> TEST_RUN_ID: ${TEST_RUN_ID}"

QUICK="${QUICK:-false}"
if [[ "${QUICK}" == "true" ]]; then
  SCALE="${SCALE:-small}"
  BURST_DURATION="${BURST_DURATION:-120}"
  RECOVERY_DURATION="${RECOVERY_DURATION:-120}"
  # small / burst: 10 devices × 5 pts/msg, 10s interval → 10*(120/10)*5 = 600 expected burst rows
  BURST_EXPECTED="${BURST_EXPECTED:-600}"
  # small / baseline: 10 devices × 5 pts/msg, 60s interval → 10*(120/60)*5 = 100 expected recovery rows
  RECOVERY_EXPECTED="${RECOVERY_EXPECTED:-100}"
else
  SCALE="${SCALE:-medium}"
  BURST_DURATION="${BURST_DURATION:-900}"
  RECOVERY_DURATION="${RECOVERY_DURATION:-900}"
  # medium / burst: 250 devices × 5 pts/msg, 10s interval → 250*(900/10)*5 = 112500 burst rows
  BURST_EXPECTED="${BURST_EXPECTED:-112500}"
  # medium / baseline: 250 devices × 5 pts/msg, 60s interval → 250*(900/60)*5 = 18750 recovery rows
  RECOVERY_EXPECTED="${RECOVERY_EXPECTED:-18750}"
fi

BURST_RUN_ID="${TEST_RUN_ID}-burst"
RECOVERY_RUN_ID="${TEST_RUN_ID}-recovery"

echo "    Scale: ${SCALE}"
echo "    Burst: ${BURST_DURATION}s, expected=${BURST_EXPECTED}"
echo "    Recovery: ${RECOVERY_DURATION}s, expected=${RECOVERY_EXPECTED}"

# ── Step 1: Verify OSS stack ──────────────────────────────────────────────────
echo "==> Checking OSS stack status..."
if [[ "${MODE}" == "parquet" ]]; then
  REQUIRED_CONTAINERS=("building-os.nats" "building-os.connector-worker" "building-os.minio")
else
  REQUIRED_CONTAINERS=("building-os.nats" "building-os.postgres")
fi
for c in "${REQUIRED_CONTAINERS[@]}"; do
  if ! docker ps --format "{{.Names}}" | grep -q "^${c}$"; then
    echo "ERROR: Container ${c} is not running." >&2
    echo "       Start it with: make local-up-oss" >&2
    echo "       For MQTT device simulation also run: make local-up-dev" >&2
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

# ── Phase 1: Burst ────────────────────────────────────────────────────────────
echo ""
echo "==> Phase 1: Burst (scale=${SCALE}, profile=burst, duration=${BURST_DURATION}s)..."
BUILDING_ID="${BURST_RUN_ID}" "${PYTHON}" "${SCRIPT_DIR}/device_load_generator.py" \
  --scale "${SCALE}" \
  --profile burst \
  --duration "${BURST_DURATION}" \
  --run-id "${BURST_RUN_ID}"

echo "==> Waiting ${SETTLE}s for burst writes to settle (${MODE})..."
sleep "${SETTLE}"

echo "==> Phase 1 quality check (expected=${BURST_EXPECTED})..."
"${PYTHON}" "${SCRIPT_DIR}/quality_checker.py" \
  --run-id "${BURST_RUN_ID}" \
  --mode "${MODE}" --building "${BURST_RUN_ID}" \
  --expected "${BURST_EXPECTED}"

BURST_RESULT="${SCRIPT_DIR}/results/${BURST_RUN_ID}/quality-check-result.json"
BURST_PASSED=$("${PYTHON}" -c "import json; d=json.load(open('${BURST_RESULT}')); print('true' if d.get('passed') else 'false')" 2>/dev/null || echo "false")

# ── Phase 2: Recovery ─────────────────────────────────────────────────────────
echo ""
echo "==> Phase 2: Recovery (scale=${SCALE}, profile=baseline, duration=${RECOVERY_DURATION}s)..."
BUILDING_ID="${RECOVERY_RUN_ID}" "${PYTHON}" "${SCRIPT_DIR}/device_load_generator.py" \
  --scale "${SCALE}" \
  --profile baseline \
  --duration "${RECOVERY_DURATION}" \
  --run-id "${RECOVERY_RUN_ID}"

echo "==> Waiting ${SETTLE}s for recovery writes to settle (${MODE})..."
sleep "${SETTLE}"

echo "==> Phase 2 quality check (expected=${RECOVERY_EXPECTED})..."
"${PYTHON}" "${SCRIPT_DIR}/quality_checker.py" \
  --run-id "${RECOVERY_RUN_ID}" \
  --mode "${MODE}" --building "${RECOVERY_RUN_ID}" \
  --expected "${RECOVERY_EXPECTED}"

RECOVERY_RESULT="${SCRIPT_DIR}/results/${RECOVERY_RUN_ID}/quality-check-result.json"
RECOVERY_PASSED=$("${PYTHON}" -c "import json; d=json.load(open('${RECOVERY_RESULT}')); print('true' if d.get('passed') else 'false')" 2>/dev/null || echo "false")

# ── Generate combined report ──────────────────────────────────────────────────
RESULT_DIR="${SCRIPT_DIR}/results/${TEST_RUN_ID}"
mkdir -p "${RESULT_DIR}"
REPORT_FILE="${RESULT_DIR}/report.md"

get_metric() {
  local file="$1" key="$2" default="${3:-N/A}"
  "${PYTHON}" -c "import json; d=json.load(open('${file}')); print(d.get('${key}', '${default}'))" 2>/dev/null || echo "${default}"
}

cat > "${REPORT_FILE}" <<EOF
# S3 Burst And Backpressure — ${TEST_RUN_ID}

**Date**: $(date -u +"%Y-%m-%d %H:%M:%S UTC")
**Scale**: ${SCALE}

## Phase 1: Burst

| Metric | Value |
|--------|-------|
| Run ID | ${BURST_RUN_ID} |
| Duration | ${BURST_DURATION}s |
| Profile | burst (10s interval) |
| Result | $([ "${BURST_PASSED}" = "true" ] && echo "✅ PASS" || echo "❌ FAIL") |
| DB Rows | $(get_metric "${BURST_RESULT}" "db_row_count") |
| Loss Rate | $(get_metric "${BURST_RESULT}" "loss_rate") |
| Duplicate Rate | $(get_metric "${BURST_RESULT}" "duplicate_rate") |
| Schema Invalid | $(get_metric "${BURST_RESULT}" "schema_invalid_count") |

## Phase 2: Recovery

| Metric | Value |
|--------|-------|
| Run ID | ${RECOVERY_RUN_ID} |
| Duration | ${RECOVERY_DURATION}s |
| Profile | baseline (60s interval) |
| Result | $([ "${RECOVERY_PASSED}" = "true" ] && echo "✅ PASS" || echo "❌ FAIL") |
| DB Rows | $(get_metric "${RECOVERY_RESULT}" "db_row_count") |
| Loss Rate | $(get_metric "${RECOVERY_RESULT}" "loss_rate") |
| Duplicate Rate | $(get_metric "${RECOVERY_RESULT}" "duplicate_rate") |
| Schema Invalid | $(get_metric "${RECOVERY_RESULT}" "schema_invalid_count") |

## Backpressure Notes

- Burst phase: $([ "${BURST_PASSED}" = "true" ] && echo "Pipeline absorbed burst without data loss" || echo "Pipeline showed data loss under burst — review NATS consumer lag")
- Recovery phase: $([ "${RECOVERY_PASSED}" = "true" ] && echo "Pipeline recovered to baseline successfully" || echo "Pipeline did not fully recover — extended lag observed")
EOF

OVERALL_PASSED="$([ "${BURST_PASSED}" = "true" ] && [ "${RECOVERY_PASSED}" = "true" ] && echo "true" || echo "false")"

echo "    Report written: ${REPORT_FILE}"

if [[ "${OVERALL_PASSED}" == "true" ]]; then
  echo ""
  echo "✅ S3 Burst And Backpressure PASSED (both phases)"
  exit 0
else
  echo "" >&2
  echo "❌ S3 Burst And Backpressure FAILED" >&2
  echo "   Burst PASS: ${BURST_PASSED}, Recovery PASS: ${RECOVERY_PASSED}" >&2
  echo "   Results: ${RESULT_DIR}/" >&2
  exit 1
fi
