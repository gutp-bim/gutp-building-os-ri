#!/usr/bin/env bash
set -euo pipefail

# S6 Point Control E2E Test
# Usage: bash Tools/e2e-performance/s6_point_control.sh [TEST_RUN_ID]
#
# Environment variables:
#   CONTROL_POINT_ID  — Writable point ID in OxiGraph (required)
#   BASE_URL          — API Server base URL (default: http://localhost:5000)
#   QUICK=true        — Shorter run for CI (VUS=2, DURATION=1m)
#   VUS               — Virtual users (default: 3)
#   DURATION          — k6 run duration (default: 3m)
#
# Prerequisites:
#   - docker compose -f docker-compose.oss.yaml up -d (NATS, OxiGraph at minimum)
#   - API Server running: DISABLE_AUTH=true dotnet run (DotNet/BuildingOS.ApiServer)
#   - ConnectorWorker running: ENABLE_SIM_CONTROL=true dotnet run (DotNet/BuildingOS.ConnectorWorker)
#   - k6 installed (https://k6.io/docs/get-started/installation/)
#
# Acceptance criteria:
#   - control_submission_duration p95 < 3000ms
#   - s6_error_rate < 1%
#   - timeout_rate < 1%

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

if [[ "${1:-}" != "" ]]; then
  TEST_RUN_ID="$1"
else
  TEST_RUN_ID="$(date +%Y%m%dT%H%M%SZ)-s6-$$"
fi

echo "==> S6 Point Control E2E"
echo "==> TEST_RUN_ID: ${TEST_RUN_ID}"

# Configure scale
QUICK="${QUICK:-false}"
if [[ "${QUICK}" == "true" ]]; then
  VUS="${VUS:-2}"
  DURATION="${DURATION:-1m}"
else
  VUS="${VUS:-3}"
  DURATION="${DURATION:-3m}"
fi

BASE_URL="${BASE_URL:-http://localhost:5000}"
CONTROL_POINT_ID="${CONTROL_POINT_ID:-sim-control-point-001}"
OXIGRAPH_URL="${OXIGRAPH_URL:-http://localhost:7878}"
PYTHON="${SCRIPT_DIR}/.venv/bin/python"

echo "    VUS: ${VUS}, Duration: ${DURATION}, BaseURL: ${BASE_URL}, Control point: ${CONTROL_POINT_ID}"

# ── Check NATS ────────────────────────────────────────────────────────────────
echo "==> Checking NATS..."
if ! docker ps --format "{{.Names}}" | grep -q "^building-os.nats$"; then
  echo "ERROR: building-os.nats is not running." >&2
  echo "       Start with: docker compose -f docker-compose.oss.yaml up -d building-os.nats" >&2
  exit 1
fi
echo "    NATS running."

# ── Check API Server ──────────────────────────────────────────────────────────
echo "==> Checking API Server at ${BASE_URL}/health..."
if ! curl -sf "${BASE_URL}/health" > /dev/null 2>&1; then
  echo "ERROR: API Server not reachable at ${BASE_URL}/health" >&2
  echo "       Start with: cd DotNet/BuildingOS.ApiServer && DISABLE_AUTH=true dotnet run --launch-profile WithLocal" >&2
  exit 1
fi
echo "    API Server reachable."

# ── Seed a writable/controllable point into the twin ──────────────────────────
# The control path 404s for unknown points and 400s for non-writable / no-gateway points, so seed a
# writable point + device + gatewayId (binding → egress ControlType). Idempotent (INSERT DATA).
echo "==> Seeding controllable point ${CONTROL_POINT_ID}..."
if [[ ! -f "${PYTHON}" ]]; then uv venv "${SCRIPT_DIR}/.venv"; fi
uv pip install -r "${SCRIPT_DIR}/requirements.txt" --python "${PYTHON}" -q
"${PYTHON}" "${SCRIPT_DIR}/seed_twin_points.py" \
  --control-point "${CONTROL_POINT_ID}" --gateway "GW-${CONTROL_POINT_ID}" --oxigraph "${OXIGRAPH_URL}" >/dev/null
echo "    Seeded."

# ── Check k6 ─────────────────────────────────────────────────────────────────
if ! command -v k6 &> /dev/null; then
  echo "ERROR: k6 is not installed." >&2
  echo "       See https://k6.io/docs/get-started/installation/" >&2
  exit 1
fi

# ── Prepare result dir ────────────────────────────────────────────────────────
RESULT_DIR="${SCRIPT_DIR}/results/${TEST_RUN_ID}"
mkdir -p "${RESULT_DIR}"

K6_OUTPUT="${RESULT_DIR}/k6-output.txt"
K6_JSON="${RESULT_DIR}/k6-summary.json"
REPORT_FILE="${RESULT_DIR}/report.md"

# ── Run k6 ───────────────────────────────────────────────────────────────────
echo "==> Running S6 k6 test (VUS=${VUS}, duration=${DURATION})..."
set +e
k6 run \
  --env BASE_URL="${BASE_URL}" \
  --env CONTROL_POINT_ID="${CONTROL_POINT_ID}" \
  --env VUS="${VUS}" \
  --env DURATION="${DURATION}" \
  --env TEST_RUN_ID="${TEST_RUN_ID}" \
  --summary-export="${K6_JSON}" \
  "${SCRIPT_DIR}/k6/s6_point_control.js" 2>&1 | tee "${K6_OUTPUT}"
K6_EXIT=$?
set -e

# ── Parse results ─────────────────────────────────────────────────────────────
PASSED="false"
P95=""
ERROR_RATE=""
TIMEOUT_RATE=""

if [[ -f "${K6_JSON}" ]]; then
  P95=$(python3 -c "
import json, sys
d = json.load(open('${K6_JSON}'))
m = d.get('metrics', {})
v = m.get('control_submission_duration', {}).get('values', {})
print(f\"{v.get('p(95)', 'N/A'):.1f}\" if isinstance(v.get('p(95)'), float) else 'N/A')
" 2>/dev/null || echo "N/A")

  ERROR_RATE=$(python3 -c "
import json
d = json.load(open('${K6_JSON}'))
m = d.get('metrics', {})
v = m.get('s6_error_rate', {}).get('values', {})
print(f\"{v.get('rate', 0)*100:.4f}%\" if isinstance(v.get('rate'), float) else 'N/A')
" 2>/dev/null || echo "N/A")

  TIMEOUT_RATE=$(python3 -c "
import json
d = json.load(open('${K6_JSON}'))
m = d.get('metrics', {})
v = m.get('timeout_rate', {}).get('values', {})
print(f\"{v.get('rate', 0)*100:.4f}%\" if isinstance(v.get('rate'), float) else 'N/A')
" 2>/dev/null || echo "N/A")

  [[ $K6_EXIT -eq 0 ]] && PASSED="true"
fi

# ── Write report ──────────────────────────────────────────────────────────────
cat > "${REPORT_FILE}" <<EOF
# S6 Point Control E2E — ${TEST_RUN_ID}

**Date**: $(date -u +"%Y-%m-%d %H:%M:%S UTC")
**VUS**: ${VUS}
**Duration**: ${DURATION}
**Control Point**: ${CONTROL_POINT_ID}
**Result**: $([ "${PASSED}" = "true" ] && echo "✅ PASS" || echo "❌ FAIL")

## Metrics

| Metric | Value | Threshold | Status |
|--------|-------|-----------|--------|
| Submission latency p95 | ${P95} ms | < 3000 ms | $([ "${PASSED}" = "true" ] && echo "✅" || echo "❌") |
| Error rate | ${ERROR_RATE} | < 1% | - |
| Timeout rate | ${TIMEOUT_RATE} | < 1% | - |

## Phases

| Phase | Description | Expected |
|-------|-------------|----------|
| A | Valid control submission (value only; egress ControlType resolved server-side) | 202 + controlId |
| B | Non-existent point | 404 or 403 |
| C | Missing value | 400 bad request |

## Notes

- Full round-trip latency (POST → NATS → handler → gRPC stream back) requires
  manual verification via the gRPC client or the web-client UI.
- Run ConnectorWorker with \`ENABLE_SIM_CONTROL=true\` and \`SIM_CONTROL_DELAY_MS=100\`
  for realistic latency simulation.
- See \`docs/architecture/oss-control-safety.md\` for safety boundary documentation.

## k6 Summary

\`\`\`json
$(cat "${K6_JSON}" 2>/dev/null | python3 -c "import json,sys; d=json.load(sys.stdin); print(json.dumps({k: v for k,v in d.get('metrics',{}).items() if k in ['control_submission_duration','s6_error_rate','timeout_rate','http_req_failed']}, indent=2))" 2>/dev/null || echo '{}')
\`\`\`
EOF

echo "    Report: ${REPORT_FILE}"

if [[ "${PASSED}" == "true" ]]; then
  echo ""
  echo "✅ S6 Point Control PASSED"
  echo "   submission p95=${P95}ms  error_rate=${ERROR_RATE}  timeout_rate=${TIMEOUT_RATE}"
  echo "   Results: ${RESULT_DIR}/"
  exit 0
else
  echo "" >&2
  echo "❌ S6 Point Control FAILED (k6 exit=${K6_EXIT})" >&2
  echo "   Results: ${RESULT_DIR}/" >&2
  exit 1
fi
