#!/usr/bin/env bash
set -euo pipefail

# S2 Baseline Throughput Test
# Usage: bash Tools/e2e-performance/s2_baseline.sh [TEST_RUN_ID]
#
# Environment variables:
#   MODE=parquet|timescale  — storage backend to drive & verify (default: parquet, the OSS default #216)
#   QUICK=true       — small scale / 5-min run for CI validation
#   SCALE            — load-generator scale (default: medium; QUICK=true → small)
#   DURATION         — duration in seconds (default: 3600; QUICK=true → 300)
#   FLUSH_WAIT       — seconds to wait for the pipeline to persist before checking
#                      (parquet default 90s — must exceed the connector-worker PARQUET_FLUSH_INTERVAL;
#                       set PARQUET_FLUSH_INTERVAL=1 on the worker for QUICK runs)
#
# Acceptance criteria:
#   - loss rate <= 1%
#   - duplicate rate <= 0.1%
#   - schema invalid count = 0

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
PYTHON="${SCRIPT_DIR}/.venv/bin/python"

if [[ "${1:-}" != "" ]]; then
  TEST_RUN_ID="$1"
else
  TEST_RUN_ID="$(date +%Y%m%dT%H%M%SZ)-s2-$$"
fi

MODE="${MODE:-parquet}"
export QUALITY_MODE="${MODE}"

# Mosquitto in the OSS stack enforces auth (allow_anonymous false). Default to the compose dev creds
# so the bridge + load generator can connect; override via MQTT_USERNAME / MQTT_PASSWORD.
export MQTT_USERNAME="${MQTT_USERNAME:-devices}"
export MQTT_PASSWORD="${MQTT_PASSWORD:-buildingos-devices}"

# Isolate this run in its own building_id= lake partition (parquet mode), so the quality checker can
# match it exactly (run_id[:8] alone is not collision-proof). The load generator reads BUILDING_ID.
export BUILDING_ID="${BUILDING_ID:-${TEST_RUN_ID}}"

echo "==> S2 Baseline Throughput (mode=${MODE})"
echo "==> TEST_RUN_ID: ${TEST_RUN_ID}"

# Configure scale / duration
QUICK="${QUICK:-false}"
if [[ "${QUICK}" == "true" ]]; then
  SCALE="${SCALE:-small}"
  DURATION="${DURATION:-300}"
  # small scale = 10 devices × 5 points/msg, baseline (60s interval)
  # Expected rows: 10 * (300/60) * 5 = 250 rows
  EXPECTED="${EXPECTED:-250}"
else
  SCALE="${SCALE:-medium}"
  DURATION="${DURATION:-3600}"
  # medium scale = 250 devices × 5 points/msg, baseline (60s interval)
  # Expected rows: 250 * (3600/60) * 5 = 75000 rows
  EXPECTED="${EXPECTED:-75000}"
fi

# Load profile (interval / points-per-msg). Default baseline preserves prior behaviour; override with
# PROFILE=mixed|burst to push past ~1,250 rows/min (medium×baseline) toward ~10,000+ rows/min — set
# EXPECTED accordingly since the auto value above assumes baseline (5 pt/msg, 60s interval).
PROFILE="${PROFILE:-baseline}"

echo "    Scale: ${SCALE}, Profile: ${PROFILE}, Duration: ${DURATION}s, Expected rows: ${EXPECTED}"

# ── Step 1: Verify OSS stack ──────────────────────────────────────────────────
echo "==> Checking OSS stack status..."
# parquet mode needs the real ConnectorWorker (ParquetLakeWriter) + MinIO lake; timescale needs postgres.
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
echo "    Required containers running."

# ── Step 2: Python venv ───────────────────────────────────────────────────────
if [[ ! -f "${PYTHON}" ]]; then
  echo "==> Creating Python venv..."
  uv venv "${SCRIPT_DIR}/.venv"
fi
echo "==> Installing Python dependencies..."
uv pip install -r "${SCRIPT_DIR}/requirements.txt" --python "${PYTHON}" -q

# ── Step 3: Apply schema (timescale mode only) ────────────────────────────────
if [[ "${MODE}" == "timescale" ]]; then
  echo "==> Applying TimescaleDB schema..."
  docker exec building-os.postgres psql -U buildingos -d buildingos \
    -c "CREATE TABLE IF NOT EXISTS telemetry (time TIMESTAMPTZ NOT NULL, point_id TEXT NOT NULL, building TEXT, device_id TEXT NOT NULL DEFAULT '', name TEXT, value DOUBLE PRECISION, data JSONB, id TEXT);" \
    -c "SELECT create_hypertable('telemetry', 'time', if_not_exists => TRUE);" \
    -c "CREATE INDEX IF NOT EXISTS idx_telemetry_data_run_id ON telemetry USING gin (data jsonb_path_ops) WHERE data IS NOT NULL;" \
    2>/dev/null || true
  echo "    Schema ready."
else
  echo "==> parquet mode: skipping TimescaleDB schema (lake is the store)."
fi

# ── Step 4: Start pipeline bridge ─────────────────────────────────────────────
# parquet mode → bridge publishes validated to NATS only; the real ParquetLakeWriter persists.
echo "==> Starting e2e pipeline bridge (PARQUET_MODE=$([[ "${MODE}" == "parquet" ]] && echo true || echo false))..."
PARQUET_MODE="$([[ "${MODE}" == "parquet" ]] && echo true || echo false)" \
  "${PYTHON}" "${SCRIPT_DIR}/e2e_pipeline_bridge.py" &
BRIDGE_PID=$!
trap 'kill ${BRIDGE_PID} 2>/dev/null || true' EXIT
sleep 3
echo "    Bridge running (PID=${BRIDGE_PID})."

# ── Step 5: Run load generator ───────────────────────────────────────────────
echo "==> Running S2 load generator (scale=${SCALE}, profile=${PROFILE}, duration=${DURATION}s)..."
"${PYTHON}" "${SCRIPT_DIR}/device_load_generator.py" \
  --scale "${SCALE}" \
  --profile "${PROFILE}" \
  --duration "${DURATION}" \
  --run-id "${TEST_RUN_ID}"

# ── Step 6: Wait for persistence ──────────────────────────────────────────────
# parquet: the writer flushes on PARQUET_FLUSH_INTERVAL (minutes) / PARQUET_FLUSH_MAX_ROWS, so wait
# longer than the flush interval. timescale: the bridge writes synchronously, 30s suffices.
if [[ "${MODE}" == "parquet" ]]; then
  FLUSH_WAIT="${FLUSH_WAIT:-90}"
else
  FLUSH_WAIT="${FLUSH_WAIT:-30}"
fi
echo "==> Waiting ${FLUSH_WAIT}s for pipeline to persist (${MODE})..."
sleep "${FLUSH_WAIT}"

# ── Step 7: Quality check ─────────────────────────────────────────────────────
echo "==> Running quality checker (mode=${MODE}, expected=${EXPECTED})..."
"${PYTHON}" "${SCRIPT_DIR}/quality_checker.py" \
  --run-id "${TEST_RUN_ID}" \
  --mode "${MODE}" \
  --expected "${EXPECTED}"

# ── Step 8: Generate report ───────────────────────────────────────────────────
RESULT_DIR="${SCRIPT_DIR}/results/${TEST_RUN_ID}"
RESULT_FILE="${RESULT_DIR}/quality-check-result.json"
REPORT_FILE="${RESULT_DIR}/report.md"

if [[ -f "${RESULT_FILE}" ]]; then
  PASSED=$("${PYTHON}" -c "import json; d=json.load(open('${RESULT_FILE}')); print('true' if d.get('passed') else 'false')")
  LOSS_RATE=$("${PYTHON}" -c "import json; d=json.load(open('${RESULT_FILE}')); print(d.get('loss_rate', 'N/A'))")
  DUP_RATE=$("${PYTHON}" -c "import json; d=json.load(open('${RESULT_FILE}')); print(d.get('duplicate_rate', 'N/A'))")
  INVALID=$("${PYTHON}" -c "import json; d=json.load(open('${RESULT_FILE}')); print(d.get('schema_invalid_count', 'N/A'))")
  DB_ROWS=$("${PYTHON}" -c "import json; d=json.load(open('${RESULT_FILE}')); print(d.get('db_row_count', 'N/A'))")
else
  PASSED="false"
fi

# Write report
mkdir -p "${RESULT_DIR}"
cat > "${REPORT_FILE}" <<EOF
# S2 Baseline Throughput — ${TEST_RUN_ID}

**Date**: $(date -u +"%Y-%m-%d %H:%M:%S UTC")
**Scale**: ${SCALE}
**Profile**: ${PROFILE}
**Duration**: ${DURATION}s
**Result**: $([ "${PASSED}" = "true" ] && echo "✅ PASS" || echo "❌ FAIL")

## Quality Metrics

| Metric | Value | Threshold | Status |
|--------|-------|-----------|--------|
| DB Rows | ${DB_ROWS} | ≥ ${EXPECTED} | $([ "${PASSED}" = "true" ] && echo "✅" || echo "❌") |
| Loss Rate | ${LOSS_RATE} | ≤ 1% | - |
| Duplicate Rate | ${DUP_RATE} | ≤ 0.1% | - |
| Schema Invalid | ${INVALID} | = 0 | - |

## Raw Results

\`\`\`json
$(cat "${RESULT_FILE}" 2>/dev/null || echo '{}')
\`\`\`
EOF

echo "    Report written: ${REPORT_FILE}"

if [[ "${PASSED}" == "true" ]]; then
  echo ""
  echo "✅ S2 Baseline Throughput PASSED"
  echo "   Results: ${RESULT_DIR}/"
  exit 0
else
  echo "" >&2
  echo "❌ S2 Baseline Throughput FAILED" >&2
  echo "   Results: ${RESULT_DIR}/" >&2
  [[ -f "${RESULT_FILE}" ]] && cat "${RESULT_FILE}" >&2
  exit 1
fi
