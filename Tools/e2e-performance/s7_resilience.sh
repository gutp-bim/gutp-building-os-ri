#!/usr/bin/env bash
set -euo pipefail

# S7 Resilience Test
# Usage: bash Tools/e2e-performance/s7_resilience.sh [TEST_RUN_ID]
#
# Environment variables:
#   QUICK=true     — shorter durations for CI validation
#
# Tests:
#   A. NATS JetStream replay — durable consumer restart delivers from position 0
#   B. TimescaleDB duplicate behavior — ON CONFLICT DO NOTHING with no unique index
#   C. Bridge restart recovery — kill bridge mid-load, restart, verify Phase 2 loss=0%
#
# Acceptance criteria:
#   - Test A: replay delivers >= n_published messages
#   - Test B: documented (dedup behavior depends on unique constraint presence)
#   - Test C: Phase 2 loss rate <= 1%

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
PYTHON="${SCRIPT_DIR}/.venv/bin/python"

if [[ "${1:-}" != "" ]]; then
  TEST_RUN_ID="$1"
else
  TEST_RUN_ID="$(date +%Y%m%dT%H%M%SZ)-s7-$$"
fi

MODE="${MODE:-parquet}"
export QUALITY_MODE="${MODE}"
export MQTT_USERNAME="${MQTT_USERNAME:-devices}"
export MQTT_PASSWORD="${MQTT_PASSWORD:-buildingos-devices}"

echo "==> S7 Resilience Test (mode=${MODE})"
echo "==> TEST_RUN_ID: ${TEST_RUN_ID}"
echo "    QUICK: ${QUICK:-false}"

# ── Step 1: Verify stack ──────────────────────────────────────────────────────
# Test A is NATS-only; Test C (bridge restart) persists via the real ParquetLakeWriter (parquet) or
# TimescaleDB. parquet mode needs connector-worker + minio; timescale needs postgres.
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

# ── Step 4: Run S7 test suite ─────────────────────────────────────────────────
RESULT_DIR="${SCRIPT_DIR}/results/${TEST_RUN_ID}"
mkdir -p "${RESULT_DIR}"
RESULT_FILE="${RESULT_DIR}/s7-resilience-result.json"

QUICK_FLAG=""
[[ "${QUICK:-false}" == "true" ]] && QUICK_FLAG="--quick"

echo "==> Running S7 resilience tests..."
"${PYTHON}" "${SCRIPT_DIR}/s7_resilience_test.py" \
  --run-id "${TEST_RUN_ID}" \
  ${QUICK_FLAG} \
  --output "${RESULT_FILE}"

PASSED=$("${PYTHON}" -c "import json; d=json.load(open('${RESULT_FILE}')); print('true' if d.get('passed') else 'false')" 2>/dev/null || echo "false")

# ── Step 5: Generate report ───────────────────────────────────────────────────
REPORT_FILE="${RESULT_DIR}/report.md"

get_test() {
  local test_id="$1" key="$2"
  "${PYTHON}" -c "import json; d=json.load(open('${RESULT_FILE}')); t=d['tests'].get('${test_id}',{}); print(t.get('${key}','N/A'))" 2>/dev/null || echo "N/A"
}

A_PASSED=$(get_test A passed)
B_ROWS=$(get_test B rows_in_db)
B_NOTES=$(get_test B notes)
C_P1_LOSS=$("${PYTHON}" -c "import json; d=json.load(open('${RESULT_FILE}')); print(d['tests']['C']['phase1']['loss_rate'])" 2>/dev/null || echo "N/A")
C_P2_LOSS=$("${PYTHON}" -c "import json; d=json.load(open('${RESULT_FILE}')); print(d['tests']['C']['phase2']['loss_rate'])" 2>/dev/null || echo "N/A")
C_PASSED=$(get_test C passed)

cat > "${REPORT_FILE}" <<EOF
# S7 Resilience — ${TEST_RUN_ID}

**Date**: $(date -u +"%Y-%m-%d %H:%M:%S UTC")
**Quick mode**: ${QUICK:-false}
**Overall**: $([ "${PASSED}" = "true" ] && echo "✅ PASS" || echo "❌ FAIL")

## Test A: NATS JetStream Replay

| 項目 | 値 |
|------|-----|
| 結果 | $([ "${A_PASSED}" = "True" ] && echo "✅ PASS" || echo "❌ FAIL") |
| Published | $(get_test A published) |
| First read | $(get_test A first_read) |
| Replay read | $(get_test A second_read) |

NATS JetStream の durable consumer が deliver_all ポリシーでポジション 0 からリプレイできることを確認。
ConnectorWorker 再起動後は JetStream 保持分のメッセージを再消費できる。

## Test B: TimescaleDB 重複挙動

| 項目 | 値 |
|------|-----|
| 挿入試行回数 | 2 |
| 実際の DB 行数 | ${B_ROWS} |
| ON CONFLICT DO NOTHING | $(get_test B dedup_via_on_conflict) |

${B_NOTES}

## Test C: Bridge 再起動後のリカバリ

| フェーズ | 損失率 |
|----------|--------|
| Phase 1 (再起動前) | ${C_P1_LOSS} |
| Phase 2 (再起動後) | ${C_P2_LOSS} |
| 結果 | $([ "${C_PASSED}" = "True" ] && echo "✅ PASS" || echo "❌ FAIL") |

**注**: MQTT QoS 0 の場合、ブリッジ停止中に発行されたメッセージは損失する（設計上）。
QoS 1 + 永続セッション（client_id 固定）を使うことで再起動後の受信が可能になる。
Phase 2（再起動後の新規 publish）は loss=0% が目標。

## Raw Results

\`\`\`json
$(cat "${RESULT_FILE}" 2>/dev/null || echo '{}')
\`\`\`
EOF

echo "    Report written: ${REPORT_FILE}"

if [[ "${PASSED}" == "true" ]]; then
  echo ""
  echo "✅ S7 Resilience PASSED"
  exit 0
else
  echo "" >&2
  echo "❌ S7 Resilience FAILED" >&2
  exit 1
fi
