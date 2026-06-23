#!/usr/bin/env bash
set -euo pipefail

# S1 Smoke E2E Orchestrator
# Run from repository root: bash Tools/e2e-performance/smoke.sh [TEST_RUN_ID]

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
PYTHON="${SCRIPT_DIR}/.venv/bin/python"

# Determine test run ID
if [[ "${1:-}" != "" ]]; then
  TEST_RUN_ID="$1"
else
  TEST_RUN_ID="$(date +%Y%m%dT%H%M%SZ)-smoke-$$"
fi

echo "==> TEST_RUN_ID: ${TEST_RUN_ID}"

# ── Step 1: Verify OSS stack is running ──────────────────────────────────────
echo "==> Checking OSS stack status..."
if ! docker compose -f "${REPO_ROOT}/docker-compose.oss.yaml" ps --format json 2>/dev/null | grep -q '"State":"running"'; then
  RUNNING_COUNT=$(docker compose -f "${REPO_ROOT}/docker-compose.oss.yaml" ps --status running 2>/dev/null | grep -c "running" || true)
  if [[ "${RUNNING_COUNT}" -eq 0 ]]; then
    echo "ERROR: OSS stack is not running." >&2
    echo "       Start it with: docker compose -f docker-compose.oss.yaml up -d" >&2
    exit 1
  fi
fi
echo "    OSS stack is running."

# ── Step 2: Ensure Python venv is ready ──────────────────────────────────────
if [[ ! -f "${PYTHON}" ]]; then
  echo "==> Creating Python venv..."
  uv venv "${SCRIPT_DIR}/.venv"
fi
echo "==> Installing Python dependencies..."
uv pip install -r "${SCRIPT_DIR}/requirements.txt" --python "${PYTHON}" -q

# ── Step 3: Apply TimescaleDB schema (idempotent) ────────────────────────────
echo "==> Applying TimescaleDB schema..."
docker exec building-os.postgres psql -U buildingos -d buildingos \
  -c "CREATE TABLE IF NOT EXISTS telemetry (
        time        TIMESTAMPTZ       NOT NULL,
        point_id    TEXT              NOT NULL,
        building    TEXT,
        device_id   TEXT              NOT NULL DEFAULT '',
        name        TEXT,
        value       DOUBLE PRECISION,
        data        JSONB,
        id          TEXT
      );" \
  -c "SELECT create_hypertable('telemetry', 'time', if_not_exists => TRUE);" \
  -c "CREATE INDEX IF NOT EXISTS idx_telemetry_point_time ON telemetry (point_id, time DESC);" \
  -c "CREATE INDEX IF NOT EXISTS idx_telemetry_data_run_id ON telemetry USING gin (data jsonb_path_ops) WHERE data IS NOT NULL;" \
  2>/dev/null || true
echo "    TimescaleDB schema ready."

# ── Step 4: Start MQTT→NATS bridge and NATS→TimescaleDB consumer ─────────────
echo "==> Starting MQTT→NATS bridge (mqtt_nats_bridge.py)..."
mkdir -p "${SCRIPT_DIR}/results/${TEST_RUN_ID}"
"${PYTHON}" "${SCRIPT_DIR}/mqtt_nats_bridge.py" \
  > "${SCRIPT_DIR}/results/${TEST_RUN_ID}/bridge.log" 2>&1 &
BRIDGE_PID=$!
echo "    MQTT bridge PID: ${BRIDGE_PID}"

echo "==> Starting NATS→TimescaleDB consumer (telemetry_consumer.py)..."
"${PYTHON}" "${SCRIPT_DIR}/telemetry_consumer.py" \
  > "${SCRIPT_DIR}/results/${TEST_RUN_ID}/consumer.log" 2>&1 &
CONSUMER_PID=$!
echo "    Telemetry consumer PID: ${CONSUMER_PID}"

# Give bridge and consumer time to connect and create NATS streams
sleep 5

# Ensure bridge and consumer started successfully
if ! kill -0 "${BRIDGE_PID}" 2>/dev/null; then
  echo "ERROR: MQTT bridge failed to start. Check log:" >&2
  cat "${SCRIPT_DIR}/results/${TEST_RUN_ID}/bridge.log" >&2
  exit 1
fi
if ! kill -0 "${CONSUMER_PID}" 2>/dev/null; then
  echo "ERROR: Telemetry consumer failed to start. Check log:" >&2
  cat "${SCRIPT_DIR}/results/${TEST_RUN_ID}/consumer.log" >&2
  exit 1
fi

echo "    MQTT bridge and connector-worker consumer are ready."

# Cleanup both processes on exit
trap '
  kill "${BRIDGE_PID}" 2>/dev/null || true
  kill "${CONSUMER_PID}" 2>/dev/null || true
  wait "${BRIDGE_PID}" 2>/dev/null || true
  wait "${CONSUMER_PID}" 2>/dev/null || true
' EXIT

# ── Step 5: Run load generator ───────────────────────────────────────────────
echo "==> Running load generator (scale=small, profile=baseline, duration=120s)..."
"${PYTHON}" "${SCRIPT_DIR}/device_load_generator.py" \
  --scale small \
  --profile baseline \
  --duration 120 \
  --run-id "${TEST_RUN_ID}"

# ── Step 6: Wait for DB writes to settle ─────────────────────────────────────
echo "==> Waiting 15s for TimescaleDB writes to settle..."
sleep 15

# ── Step 7: Stop bridge and consumer ─────────────────────────────────────────
echo "==> Stopping MQTT bridge and telemetry consumer..."
kill -TERM "${BRIDGE_PID}" 2>/dev/null || true
kill -TERM "${CONSUMER_PID}" 2>/dev/null || true
# Wait up to 8s for graceful shutdown, then force-kill
for _i in 1 2 3 4 5 6 7 8; do
  _alive=0
  kill -0 "${BRIDGE_PID}" 2>/dev/null && _alive=1
  kill -0 "${CONSUMER_PID}" 2>/dev/null && _alive=1
  [[ "${_alive}" -eq 0 ]] && break
  sleep 1
done
kill -KILL "${BRIDGE_PID}" 2>/dev/null || true
kill -KILL "${CONSUMER_PID}" 2>/dev/null || true
wait "${BRIDGE_PID}" 2>/dev/null || true
wait "${CONSUMER_PID}" 2>/dev/null || true
trap - EXIT

# ── Step 8: Check API Server health (optional — skip if not running) ──────────
API_BASE="${API_BASE_URL:-http://localhost:5000}"
if curl -sf "${API_BASE}/health" >/dev/null 2>&1; then
  echo "==> API Server is healthy at ${API_BASE}/health"
else
  echo "    API Server not reachable at ${API_BASE}/health — quality check will skip API verification"
  API_BASE=""
fi

# ── Step 9: Quality check ────────────────────────────────────────────────────
echo "==> Running quality checker (expected=50)..."
QC_ARGS=(--run-id "${TEST_RUN_ID}" --expected 50)
[[ -n "${API_BASE}" ]] && QC_ARGS+=(--api-base "${API_BASE}")
"${PYTHON}" "${SCRIPT_DIR}/quality_checker.py" "${QC_ARGS[@]}"

# ── Step 10: Evaluate result ──────────────────────────────────────────────────
RESULT_FILE="${SCRIPT_DIR}/results/${TEST_RUN_ID}/quality-check-result.json"

if [[ ! -f "${RESULT_FILE}" ]]; then
  echo "❌ Smoke E2E FAILED — quality-check-result.json not found at ${RESULT_FILE}" >&2
  exit 1
fi

PASSED=$("${PYTHON}" -c "import json,sys; d=json.load(open('${RESULT_FILE}')); print('true' if d.get('passed') else 'false')")

if [[ "${PASSED}" == "true" ]]; then
  echo ""
  echo "✅ Smoke E2E PASSED"
  echo "   Results: ${SCRIPT_DIR}/results/${TEST_RUN_ID}/"
  exit 0
else
  echo "" >&2
  echo "❌ Smoke E2E FAILED" >&2
  echo "   Results: ${SCRIPT_DIR}/results/${TEST_RUN_ID}/" >&2
  cat "${RESULT_FILE}" >&2
  exit 1
fi
