#!/usr/bin/env bash
# E3 — 最新値の鮮度 / stale 率 (#241). Enables the gRPC ingress + hot KV path on the connector, then
# runs s13_latest_freshness.py.
#
# Usage: bash s13_latest_freshness.sh [OUT_DIR]   (TRIALS default 60; GRPC_INGRESS_PORT default 5051)
set -euo pipefail

REPO_ROOT="$(git rev-parse --show-toplevel)"
PERF="$REPO_ROOT/Tools/e2e-performance"
OUT="${1:-$PERF/results/E3-freshness-$(date +%Y%m%d-%H%M%S)}"
mkdir -p "$OUT"

export GRPC_INGRESS_PORT="${GRPC_INGRESS_PORT:-5051}"
COMPOSE_FILE="${COMPOSE_FILE:-$REPO_ROOT/docker-compose.oss.yaml}"
TRIALS="${TRIALS:-60}"

PYTHON_VENV="$PERF/.venv/bin/python"
[[ -x "$PYTHON_VENV" ]] || uv venv "$PERF/.venv"
uv pip install -r "$PERF/requirements.txt" --python "$PYTHON_VENV" -q

echo "[s13] enabling gRPC ingress on connector-worker (GRPC_INGRESS_PORT=$GRPC_INGRESS_PORT)"
GRPC_INGRESS_PORT="$GRPC_INGRESS_PORT" docker compose -f "$COMPOSE_FILE" up -d --force-recreate \
  --no-deps building-os.connector-worker
sleep 12

"$PYTHON_VENV" "$PERF/s13_latest_freshness.py" \
  --out "$OUT" --trials "$TRIALS" \
  --ingress "localhost:${GRPC_INGRESS_PORT}" \
  --base-url "${BASE_URL:-http://localhost:5000}" \
  --oxigraph "${OXIGRAPH_URL:-http://localhost:7878}"
rc=$?
echo "[s13] E3 freshness done → $OUT (rc=$rc)"
exit $rc
