#!/usr/bin/env bash
set -euo pipefail

# Production-scale S2 orchestrator (#297).
# Runs the sustained throughput/quality test AND captures the three KPIs the architecture review
# asked for, in one pass, on a dedicated bench host:
#
#   1. consumer pending (NATS :8222)         → kpi_sampler.py during the run
#   2. ingest→queryable freshness lag p95    → kpi_sampler.py via Prometheus (PARQUET_FLUSH_INTERVAL=5)
#   3. range query p95 vs building/point     → s2_scale_sweep.sh (run separately; pointer printed)
#
# IMPORTANT — throughput target. medium × baseline = 250 dev × 5 pt / 60s ≈ 1,250 rows/min
# (75,000 rows/h). To substantiate "~10,000 rows/min" pick a heavier shape, e.g.:
#   SCALE=large  PROFILE=baseline   → 1000 dev × 5 / 60s ≈  5,000/min
#   SCALE=medium PROFILE=mixed      →  250 dev × 25 / 30s ≈ 12,500/min
#   SCALE=large  PROFILE=mixed      → 1000 dev × 25 / 30s ≈ 50,000/min
# Verify your bench host can sustain the chosen shape (load gen is the floor; see RISKS in the runbook).
#
# Usage: bash Tools/e2e-performance/run_s2_production.sh [TEST_RUN_ID]
# Env:
#   SCALE (default medium) / PROFILE (baseline) / DURATION (3600) / EXPECTED (auto from scale)
#   PARQUET_FLUSH_INTERVAL  expected on the connector-worker (default note: 5) — set BEFORE compose up
#   PROMETHEUS_URL          enables the freshness-lag KPI (default http://localhost:9090)
#   SAMPLE_INTERVAL         KPI poll seconds (default 15)
#
# Prereq: OSS stack up with PARQUET_FLUSH_INTERVAL=5 and --profile mqtt, e.g.
#   MQTT_HOST=building-os.mosquitto PARQUET_FLUSH_INTERVAL=5 \
#     docker compose -f docker-compose.oss.yaml --profile mqtt up -d

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PYTHON="${SCRIPT_DIR}/.venv/bin/python"

TEST_RUN_ID="${1:-$(date +%Y%m%dT%H%M%SZ)-s2prod-$$}"
export SCALE="${SCALE:-medium}"
export PROFILE="${PROFILE:-baseline}"
export DURATION="${DURATION:-3600}"
PROMETHEUS_URL="${PROMETHEUS_URL:-http://localhost:9090}"
SAMPLE_INTERVAL="${SAMPLE_INTERVAL:-15}"
FLUSH_INTERVAL_MIN="${PARQUET_FLUSH_INTERVAL:-5}"

RESULT_DIR="${SCRIPT_DIR}/results/${TEST_RUN_ID}"
mkdir -p "${RESULT_DIR}"

echo "==> Production S2 (#297) run=${TEST_RUN_ID} scale=${SCALE} profile=${PROFILE} duration=${DURATION}s"
echo "    flush interval (min)=${FLUSH_INTERVAL_MIN}  prometheus=${PROMETHEUS_URL}"

if [[ ! -f "${PYTHON}" ]]; then uv venv "${SCRIPT_DIR}/.venv"; fi
uv pip install -r "${SCRIPT_DIR}/requirements.txt" --python "${PYTHON}" -q

# ── Start the KPI sampler in the background (pending + freshness lag) ──────────
echo "==> Starting KPI sampler (interval=${SAMPLE_INTERVAL}s)..."
"${PYTHON}" "${SCRIPT_DIR}/kpi_sampler.py" \
  --out "${RESULT_DIR}" \
  --interval "${SAMPLE_INTERVAL}" \
  --prometheus "${PROMETHEUS_URL}" \
  --flush-interval-min "${FLUSH_INTERVAL_MIN}" &
SAMPLER_PID=$!
trap 'kill -TERM ${SAMPLER_PID} 2>/dev/null || true' EXIT

# ── Run the sustained throughput + quality test ───────────────────────────────
# s2_baseline.sh honours SCALE/PROFILE/DURATION/EXPECTED; flush wait must exceed the 5-min flush.
echo "==> Running s2_baseline.sh (this is the long pass)..."
FLUSH_WAIT="${FLUSH_WAIT:-$(( FLUSH_INTERVAL_MIN * 60 + 90 ))}" \
PROFILE="${PROFILE}" SCALE="${SCALE}" DURATION="${DURATION}" \
  bash "${SCRIPT_DIR}/s2_baseline.sh" "${TEST_RUN_ID}" || S2_RC=$?
S2_RC="${S2_RC:-0}"

# ── Stop the sampler, let it write its summary ────────────────────────────────
echo "==> Stopping KPI sampler..."
kill -TERM ${SAMPLER_PID} 2>/dev/null || true
wait ${SAMPLER_PID} 2>/dev/null || true
trap - EXIT

# ── Storage KPI snapshot ──────────────────────────────────────────────────────
echo "==> Lake storage snapshot..."
bash "${SCRIPT_DIR}/measure_lake_storage.sh" > "${RESULT_DIR}/lake-storage.txt" 2>&1 || true

# ── Aggregate report ──────────────────────────────────────────────────────────
REPORT="${RESULT_DIR}/production-report.md"
QUALITY="${RESULT_DIR}/quality-check-result.json"
KPI="${RESULT_DIR}/kpi-summary.json"
{
  echo "# Production S2 report — ${TEST_RUN_ID}"
  echo
  echo "- Date: $(date -u +'%Y-%m-%d %H:%M:%S UTC')"
  echo "- Shape: scale=${SCALE} profile=${PROFILE} duration=${DURATION}s flush=${FLUSH_INTERVAL_MIN}min"
  echo "- s2_baseline exit: ${S2_RC} (0 = quality PASS)"
  echo
  echo "## ① Throughput / quality (s2_baseline)"
  echo '```json'
  cat "${QUALITY}" 2>/dev/null || echo '{ "note": "quality-check-result.json missing" }'
  echo '```'
  echo
  echo "## ②/① KPI sampler (consumer pending + freshness lag)"
  echo '```json'
  cat "${KPI}" 2>/dev/null || echo '{ "note": "kpi-summary.json missing" }'
  echo '```'
  echo
  echo "## Lake storage"
  echo '```'
  cat "${RESULT_DIR}/lake-storage.txt" 2>/dev/null || echo "n/a"
  echo '```'
  echo
  echo "## ③ Range p95 vs scale"
  echo "Run separately: \`SWEEP='1 3 10' bash Tools/e2e-performance/s2_scale_sweep.sh ${TEST_RUN_ID}-sweep\`"
  echo "→ results/${TEST_RUN_ID}-sweep/scale-sweep.md"
} > "${REPORT}"

echo "==> Report → ${REPORT}"
exit "${S2_RC}"
