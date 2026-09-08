#!/usr/bin/env bash
# E12 — mixed-load benchmark: ingress/control tail latency during compaction, WORKER_ROLE=all vs
# role-split (#401, child ② of the #399 Capability-based Worker Runtime PRD). CAPPED-RUN by default
# (DURATION_S well under 10 minutes) — same precedent E10/E11 set with their own capped-run variants:
# gate on what a short run can actually prove (compaction-window latency comparison, control RTT
# with/without concurrent load), not on how long a true multi-hour/production benchmark would run.
#
# Brings the stack up in either the all-in-one or role-split shape (ROLE_MODE), sets a fast
# flush/compaction cadence so a short run actually sees a compaction cycle (same technique
# s20_retention_compaction.sh uses), and runs s21_mixed_load_benchmark.py, which internally:
#   1. runs k6/s6_point_control.js alone for a control-RTT baseline (no concurrent ingest),
#   2. then runs sustained gRPC ingest + k6 control load concurrently while forcing a compaction
#      cycle mid-run (s20's settled_target_hour technique) and sampling CPU/mem/JetStream consumer
#      lag throughout,
#   3. computes the #399 split-decision verdict and writes kpi-summary.json + report.md.
#
# Usage: bash s21_mixed_load_benchmark.sh [OUT_DIR]
#        ROLE_MODE=split bash s21_mixed_load_benchmark.sh [OUT_DIR]
#        DURATION_S=180 INGEST_RATE=30 bash s21_mixed_load_benchmark.sh [OUT_DIR]
#        CONTROL_POINT_ID=<existing-writable-point> bash s21_mixed_load_benchmark.sh [OUT_DIR]
set -euo pipefail

REPO_ROOT="$(git rev-parse --show-toplevel)"
PERF="$REPO_ROOT/Tools/e2e-performance"
OUT="${1:-$PERF/results/E12-$(date +%Y%m%d-%H%M%S)}"
mkdir -p "$OUT"

export GRPC_INGRESS_PORT="${GRPC_INGRESS_PORT:-5051}"
COMPOSE_FILE="${COMPOSE_FILE:-$REPO_ROOT/docker-compose.oss.yaml}"
ROLES_COMPOSE_FILE="${ROLES_COMPOSE_FILE:-$REPO_ROOT/docker-compose.roles.yaml}"
ROLE_MODE="${ROLE_MODE:-all}"

DURATION_S="${DURATION_S:-300}"
INGEST_RATE="${INGEST_RATE:-20}"
INGEST_POINTS="${INGEST_POINTS:-50}"
CONTROL_VUS="${CONTROL_VUS:-3}"
CONTROL_BASELINE_DURATION_S="${CONTROL_BASELINE_DURATION_S:-45}"
TARGET_HOURS_BACK="${TARGET_HOURS_BACK:-3}"

# Fast cadence so a short run actually sees a flush + compaction cycle without waiting out the app
# defaults (flush 5min / compaction scan 15min / settle grace 30min) — same technique
# s20_retention_compaction.sh uses. SETTLE_MINUTES=0 is safe here for the same reason it is there:
# the forced-compaction wave places data in an ALREADY wall-clock-settled hour (settled_target_hour),
# so it does not depend on a real settle grace to fire fast. This does NOT affect the sustained
# ingest points' own (unforced) partitions — they simply flush/compact faster too, which is fine
# since this axis's ingest KPI is latency, not compaction correctness.
export PARQUET_FLUSH_INTERVAL="${PARQUET_FLUSH_INTERVAL:-1}"
export LAKE_COMPACTION_INTERVAL="${LAKE_COMPACTION_INTERVAL:-1}"
export LAKE_COMPACTION_SETTLE_MINUTES="${LAKE_COMPACTION_SETTLE_MINUTES:-0}"
export DISABLE_AUTH="${DISABLE_AUTH:-true}"

if ! command -v k6 &> /dev/null; then
  echo "ERROR: k6 is not installed (control load generator)." >&2
  echo "       See https://k6.io/docs/get-started/installation/" >&2
  exit 1
fi

PYTHON_VENV="$PERF/.venv/bin/python"
[[ -x "$PYTHON_VENV" ]] || uv venv "$PERF/.venv"
uv pip install -r "$PERF/requirements.txt" --python "$PYTHON_VENV" -q

echo "[s21] role_mode=$ROLE_MODE duration=${DURATION_S}s ingest_rate=${INGEST_RATE}/s"
echo "[s21]   flush=${PARQUET_FLUSH_INTERVAL}min compaction=${LAKE_COMPACTION_INTERVAL}min/settle=${LAKE_COMPACTION_SETTLE_MINUTES}min"
echo "[s21]   -> $OUT"

CONTROL_POINT_ARGS=()
if [[ -n "${CONTROL_POINT_ID:-}" ]]; then
  CONTROL_POINT_ARGS=(--control-point-id "$CONTROL_POINT_ID")
fi

rc=0
"$PYTHON_VENV" "$PERF/s21_mixed_load_benchmark.py" \
  --out "$OUT" --role-mode "$ROLE_MODE" \
  --duration-s "$DURATION_S" --ingest-rate "$INGEST_RATE" --ingest-points "$INGEST_POINTS" \
  --control-vus "$CONTROL_VUS" --control-baseline-duration-s "$CONTROL_BASELINE_DURATION_S" \
  --target-hours-back "$TARGET_HOURS_BACK" \
  --compose-file "$COMPOSE_FILE" --roles-compose-file "$ROLES_COMPOSE_FILE" \
  --ingress "localhost:${GRPC_INGRESS_PORT}" \
  --oxigraph "${OXIGRAPH_URL:-http://localhost:7878}" \
  --minio-endpoint "${MINIO_ENDPOINT_HOST:-localhost:9000}" \
  --base-url "${BASE_URL:-http://localhost:5000}" \
  "${CONTROL_POINT_ARGS[@]}" || rc=$?
echo "[s21] E12 mixed-load benchmark done → $OUT (rc=$rc)"
exit $rc
