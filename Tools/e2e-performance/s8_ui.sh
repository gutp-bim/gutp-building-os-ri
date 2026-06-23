#!/usr/bin/env bash
set -euo pipefail

# S8 UI Journey Test (Playwright)
# Usage: bash Tools/e2e-performance/s8_ui.sh [TEST_RUN_ID]
#
# Environment variables:
#   BASE_URL           — web-client URL (default: http://localhost:3000)
#   ADMIN_CONSOLE_URL  — (admin) workspace URL (default: ${BASE_URL}/admin)
#   KEYCLOAK_URL       — Keycloak URL (default: http://localhost:8080)
#   KEYCLOAK_REALM     — realm name (default: building-os)
#   TEST_USER          — login username (default: admin)
#   TEST_PASSWORD      — login password (default: admin)
#   SKIP_START_SERVERS — set to "true" to skip starting web-client
#
# Prerequisites:
#   - OSS stack partially running (Keycloak started by this script if needed)
#   - Node.js 22+, npm
#   - Playwright Chromium: npx playwright install chromium

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/../.." && pwd)"
PLAYWRIGHT_DIR="${SCRIPT_DIR}/playwright"

if [[ "${1:-}" != "" ]]; then
  TEST_RUN_ID="$1"
else
  TEST_RUN_ID="$(date +%Y%m%dT%H%M%SZ)-s8-$$"
fi

BASE_URL="${BASE_URL:-http://localhost:3000}"
ADMIN_CONSOLE_URL="${ADMIN_CONSOLE_URL:-${BASE_URL}/admin}"
KEYCLOAK_URL="${KEYCLOAK_URL:-http://localhost:8080}"
KEYCLOAK_REALM="${KEYCLOAK_REALM:-building-os}"
TEST_USER="${TEST_USER:-admin}"
TEST_PASSWORD="${TEST_PASSWORD:-admin}"
SKIP_START_SERVERS="${SKIP_START_SERVERS:-false}"

echo "==> S8 UI Journey Test (Playwright)"
echo "==> TEST_RUN_ID: ${TEST_RUN_ID}"
echo "    BASE_URL: ${BASE_URL}"
echo "    ADMIN_CONSOLE_URL: ${ADMIN_CONSOLE_URL}"
echo "    KEYCLOAK_URL: ${KEYCLOAK_URL}"

RESULT_DIR="${SCRIPT_DIR}/results/${TEST_RUN_ID}"
mkdir -p "${RESULT_DIR}"

WEB_CLIENT_PID=""
KEYCLOAK_STARTED=false

cleanup() {
  [[ -n "${WEB_CLIENT_PID}" ]] && kill "${WEB_CLIENT_PID}" 2>/dev/null || true
  if [[ "${KEYCLOAK_STARTED}" == "true" ]]; then
    echo "==> Keycloak was started by this script (left running for reuse)"
  fi
}
trap cleanup EXIT

# ── Step 1: Ensure Keycloak is running ────────────────────────────────────────
if ! docker ps --format "{{.Names}}" | grep -q "^building-os.keycloak$"; then
  echo "==> Starting Keycloak..."
  docker compose -f "${REPO_ROOT}/docker-compose.oss.yaml" up -d building-os.keycloak
  KEYCLOAK_STARTED=true
  echo "    Waiting for Keycloak to be ready (up to 120s)..."
  for i in $(seq 1 60); do
    if curl -sf "${KEYCLOAK_URL}/realms/${KEYCLOAK_REALM}" >/dev/null 2>&1; then
      echo "    Keycloak ready."
      break
    fi
    sleep 2
    if [[ $i -eq 60 ]]; then
      echo "ERROR: Keycloak did not become ready in time." >&2
      exit 1
    fi
  done
else
  echo "==> Keycloak already running."
  # Wait for the realm to be available
  for i in $(seq 1 30); do
    if curl -sf "${KEYCLOAK_URL}/realms/${KEYCLOAK_REALM}" >/dev/null 2>&1; then
      break
    fi
    sleep 2
    if [[ $i -eq 30 ]]; then
      echo "ERROR: Keycloak realm not available." >&2
      exit 1
    fi
  done
fi

KEYCLOAK_AUTHORITY="${KEYCLOAK_URL}/realms/${KEYCLOAK_REALM}"

# ── Step 2: Start web-client if needed (serves the (admin) workspace at /admin) ──
if [[ "${SKIP_START_SERVERS}" != "true" ]]; then
  # Check if web-client is already running
  if curl -sf "${BASE_URL}" >/dev/null 2>&1; then
    echo "==> web-client already running at ${BASE_URL}"
  else
    echo "==> Starting web-client dev server on port 3000..."
    cd "${REPO_ROOT}/web-client"
    NEXT_PUBLIC_KEYCLOAK_AUTHORITY="${KEYCLOAK_AUTHORITY}" \
    NEXT_PUBLIC_KEYCLOAK_CLIENT_ID="web-client" \
      npm run dev -- --port 3000 > "${RESULT_DIR}/web-client.log" 2>&1 &
    WEB_CLIENT_PID=$!
    cd "${REPO_ROOT}"

    echo "    Waiting for web-client (up to 90s)..."
    for i in $(seq 1 45); do
      if curl -sf "${BASE_URL}" >/dev/null 2>&1; then
        echo "    web-client ready."
        break
      fi
      sleep 2
      if [[ $i -eq 45 ]]; then
        echo "ERROR: web-client did not start in time. Check ${RESULT_DIR}/web-client.log" >&2
        exit 1
      fi
    done
  fi
else
  echo "==> Skipping server startup (SKIP_START_SERVERS=true)"
fi

# ── Step 3: Run Playwright tests ─────────────────────────────────────────────
echo ""
echo "==> Running Playwright S8 UI tests..."
cd "${PLAYWRIGHT_DIR}"

PLAYWRIGHT_EXIT=0
BASE_URL="${BASE_URL}" \
ADMIN_CONSOLE_URL="${ADMIN_CONSOLE_URL}" \
KEYCLOAK_URL="${KEYCLOAK_URL}" \
KEYCLOAK_REALM="${KEYCLOAK_REALM}" \
TEST_USER="${TEST_USER}" \
TEST_PASSWORD="${TEST_PASSWORD}" \
  npx playwright test \
    --reporter=html,junit \
    --output="${RESULT_DIR}/playwright-test-results" \
    2>&1 | tee "${RESULT_DIR}/playwright-output.txt" || PLAYWRIGHT_EXIT=$?

cd "${REPO_ROOT}"

# Copy HTML report and JUnit XML to result dir
cp -r "${SCRIPT_DIR}/results/playwright-report" "${RESULT_DIR}/playwright-report" 2>/dev/null || true
cp "${SCRIPT_DIR}/results/playwright-results.xml" "${RESULT_DIR}/playwright-results.xml" 2>/dev/null || true

# ── Step 4: Parse results ─────────────────────────────────────────────────────
TOTAL_COUNT=0
FAILED_COUNT=0
SKIPPED_COUNT=0
ERRORS_COUNT=0
if [[ -f "${RESULT_DIR}/playwright-results.xml" ]]; then
  TOTAL_COUNT=$(grep -o 'tests="[0-9]*"' "${RESULT_DIR}/playwright-results.xml" | grep -o '[0-9]*' | head -1 || echo 0)
  FAILED_COUNT=$(grep -o 'failures="[0-9]*"' "${RESULT_DIR}/playwright-results.xml" | grep -o '[0-9]*' | head -1 || echo 0)
  SKIPPED_COUNT=$(grep -o 'skipped="[0-9]*"' "${RESULT_DIR}/playwright-results.xml" | grep -o '[0-9]*' | head -1 || echo 0)
  ERRORS_COUNT=$(grep -o 'errors="[0-9]*"' "${RESULT_DIR}/playwright-results.xml" | grep -o '[0-9]*' | head -1 || echo 0)
fi
PASSED_COUNT=$(( TOTAL_COUNT - FAILED_COUNT - SKIPPED_COUNT - ERRORS_COUNT ))

OVERALL="$([ "${PLAYWRIGHT_EXIT}" -eq 0 ] && echo "PASS" || echo "FAIL")"

# ── Step 5: Generate report ───────────────────────────────────────────────────
REPORT_FILE="${RESULT_DIR}/report.md"

cat > "${REPORT_FILE}" <<EOF
# S8 UI Journey — ${TEST_RUN_ID}

**Date**: $(date -u +"%Y-%m-%d %H:%M:%S UTC")
**Overall**: $([ "${PLAYWRIGHT_EXIT}" -eq 0 ] && echo "✅ PASS" || echo "❌ FAIL")

## Test Results

| Metric | Value |
|--------|-------|
| Tests passed | ${PASSED_COUNT} |
| Tests failed | ${FAILED_COUNT} |
| Tests skipped | ${SKIPPED_COUNT} |
| Exit code | ${PLAYWRIGHT_EXIT} |

## Test Scenarios

| Test | Description |
|------|-------------|
| login and reach dashboard | Keycloak login → dashboard main content visible (p95 ≤ 3s) |
| navigate buildings | Buildings list or /buildings route loads (p95 ≤ 3s) |
| navigate to devices and points | Device/floor/space navigation, point detail (p95 ≤ 3s) |
| (admin) workspace loads | web-client /admin renders title/heading (p95 ≤ 3s) |

## Environment

| Variable | Value |
|----------|-------|
| BASE_URL | ${BASE_URL} |
| ADMIN_CONSOLE_URL | ${ADMIN_CONSOLE_URL} |
| KEYCLOAK_URL | ${KEYCLOAK_URL} |
| KEYCLOAK_REALM | ${KEYCLOAK_REALM} |

## Playwright Output

\`\`\`
$(tail -50 "${RESULT_DIR}/playwright-output.txt" 2>/dev/null || echo "(no output)")
\`\`\`
EOF

echo ""
echo "    Report written: ${REPORT_FILE}"

if [[ "${PLAYWRIGHT_EXIT}" -eq 0 ]]; then
  echo ""
  echo "✅ S8 UI Journey PASSED"
  exit 0
else
  echo "" >&2
  echo "❌ S8 UI Journey FAILED (exit=${PLAYWRIGHT_EXIT})" >&2
  exit "${PLAYWRIGHT_EXIT}"
fi
