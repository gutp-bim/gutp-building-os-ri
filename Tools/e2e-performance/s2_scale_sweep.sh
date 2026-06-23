#!/usr/bin/env bash
set -euo pipefail

# Range-query p95 vs building/point scale (#297 KPI ③).
# Measures whether /telemetries/query range p95 stays within threshold as the number of distinct
# buildings/points in the twin + lake grows (exercises the point→building pruning, #273).
#
# For each step N in SWEEP (cumulative building count), it: loads a short wide burst into the new
# building partitions, waits for the flush, seeds those points into the twin, then runs the k6 range
# probe against a sample of points across ALL buildings so far, recording range p95 keyed by N.
#
# Usage: bash Tools/e2e-performance/s2_scale_sweep.sh [BASE_RUN_ID]
# Env:
#   SWEEP                building-count steps (default "1 3 10")
#   POINTS_PER_BUILDING  points seeded per building (default 250 → mirrors medium device fan-out)
#   BASE_URL             API base (default http://localhost:5000)
#   OXIGRAPH_URL         twin SPARQL base (default http://localhost:7878)
#   LOAD_DURATION        per-building warm-up load seconds (default 60)
#   FLUSH_WAIT           flush wait seconds (default 90; needs PARQUET_FLUSH_INTERVAL small)
#   VUS / DURATION       k6 shape for each step (default 10 / 60s)
#
# Acceptance (per step): range p95 < 2000ms. Output: results/<base>/scale-sweep.jsonl + scale-sweep.md.

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PYTHON="${SCRIPT_DIR}/.venv/bin/python"

BASE_RUN_ID="${1:-$(date +%Y%m%dT%H%M%SZ)-sweep-$$}"
SWEEP="${SWEEP:-1 3 10}"
POINTS_PER_BUILDING="${POINTS_PER_BUILDING:-250}"
BASE_URL="${BASE_URL:-http://localhost:5000}"
OXIGRAPH_URL="${OXIGRAPH_URL:-http://localhost:7878}"
LOAD_DURATION="${LOAD_DURATION:-60}"
FLUSH_WAIT="${FLUSH_WAIT:-90}"
VUS="${VUS:-10}"
DURATION="${DURATION:-60s}"

export MQTT_USERNAME="${MQTT_USERNAME:-devices}"
export MQTT_PASSWORD="${MQTT_PASSWORD:-buildingos-devices}"

RESULT_DIR="${SCRIPT_DIR}/results/${BASE_RUN_ID}"
mkdir -p "${RESULT_DIR}"
SWEEP_JSONL="${RESULT_DIR}/scale-sweep.jsonl"
: > "${SWEEP_JSONL}"

echo "==> Scale sweep (KPI ③) base=${BASE_RUN_ID} steps='${SWEEP}' points/building=${POINTS_PER_BUILDING}"

for c in building-os.nats building-os.connector-worker building-os.minio building-os.api; do
  docker ps --format "{{.Names}}" | grep -q "^${c}$" || { echo "ERROR: ${c} not running" >&2; exit 1; }
done

if [[ ! -f "${PYTHON}" ]]; then uv venv "${SCRIPT_DIR}/.venv"; fi
uv pip install -r "${SCRIPT_DIR}/requirements.txt" --python "${PYTHON}" -q

# Cumulative pools across steps.
ALL_POINT_IDS=""
PREV=0
for N in ${SWEEP}; do
  echo "==> Step N=${N} buildings (adding $((N - PREV)))"
  for ((i = PREV + 1; i <= N; i++)); do
    RUN="${BASE_RUN_ID}-b${i}"
    export BUILDING_ID="${RUN}"
    echo "    building ${i}: load (${LOAD_DURATION}s wide) → flush → seed"
    PARQUET_MODE=true "${PYTHON}" "${SCRIPT_DIR}/e2e_pipeline_bridge.py" &
    BRIDGE_PID=$!
    sleep 3
    "${PYTHON}" "${SCRIPT_DIR}/device_load_generator.py" \
      --scale small --profile wide --duration "${LOAD_DURATION}" --run-id "${RUN}" || true
    kill ${BRIDGE_PID} 2>/dev/null || true
    PTS="$("${PYTHON}" "${SCRIPT_DIR}/seed_twin_points.py" \
      --run-id "${RUN}" --devices 10 --points-per-device $(( (POINTS_PER_BUILDING + 9) / 10 )) \
      --oxigraph "${OXIGRAPH_URL}" | tail -1)"
    ALL_POINT_IDS="${ALL_POINT_IDS:+${ALL_POINT_IDS},}${PTS}"
  done
  PREV="${N}"

  echo "    waiting ${FLUSH_WAIT}s for flush before probing N=${N}..."
  sleep "${FLUSH_WAIT}"

  STEP_DIR="${RESULT_DIR}/step-${N}"
  mkdir -p "${STEP_DIR}"
  echo "    k6 range probe (VUS=${VUS}, ${DURATION}) across ${N} buildings..."
  BASE_URL="${BASE_URL}" VUS="${VUS}" DURATION="${DURATION}" POINT_IDS="${ALL_POINT_IDS}" \
    TEST_RUN_ID="${BASE_RUN_ID}-N${N}" \
    k6 run --summary-export="${STEP_DIR}/k6-summary.json" "${SCRIPT_DIR}/k6/s5_api_read.js" \
    | tee "${STEP_DIR}/k6-output.txt" || true

  RANGE_P95="$("${PYTHON}" - "${STEP_DIR}/k6-summary.json" <<'PY'
import json, sys
try:
    d = json.load(open(sys.argv[1]))
    m = d.get("metrics", {})
    # k6 trend metric named "range_query_duration" in s5_api_read.js; fall back to http_req_duration
    t = m.get("range_query_duration") or m.get("http_req_duration") or {}
    print(t.get("p(95)", t.get("p95", "")))
except Exception:
    print("")
PY
)"
  echo "{\"buildings\": ${N}, \"points\": $(( N * POINTS_PER_BUILDING )), \"range_p95_ms\": \"${RANGE_P95}\"}" >> "${SWEEP_JSONL}"
  echo "    N=${N}: range p95 = ${RANGE_P95} ms"
done

# ── Markdown summary ──────────────────────────────────────────────────────────
{
  echo "# Scale sweep — range query p95 vs building/point count (#297 KPI ③)"
  echo
  echo "Base run: \`${BASE_RUN_ID}\` · points/building: ${POINTS_PER_BUILDING} · threshold: range p95 < 2000ms"
  echo
  echo "| buildings | points | range p95 (ms) |"
  echo "|--:|--:|--:|"
  "${PYTHON}" - "${SWEEP_JSONL}" <<'PY'
import json, sys
for line in open(sys.argv[1]):
    line = line.strip()
    if not line:
        continue
    d = json.loads(line)
    print(f"| {d['buildings']} | {d['points']} | {d['range_p95_ms']} |")
PY
} > "${RESULT_DIR}/scale-sweep.md"

echo "==> Scale sweep done → ${RESULT_DIR}/scale-sweep.md"
