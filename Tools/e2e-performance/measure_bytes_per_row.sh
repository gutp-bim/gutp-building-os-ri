#!/usr/bin/env bash
# E7 — parquet vs TimescaleDB bytes/row (#245). Enables the gRPC ingress on the connector, then runs
# measure_bytes_per_row.py (ingest ~N rows → parquet bytes/row; same rows into a postgres heap →
# TimescaleDB uncompressed bytes/row; ratio).
#
# Usage: bash measure_bytes_per_row.sh [OUT_DIR]   (ROWS default 50000; GRPC_INGRESS_PORT 5051)
set -euo pipefail

REPO_ROOT="$(git rev-parse --show-toplevel)"
PERF="$REPO_ROOT/Tools/e2e-performance"
OUT="${1:-$PERF/results/E7-bytesrow-$(date +%Y%m%d-%H%M%S)}"
mkdir -p "$OUT"

export GRPC_INGRESS_PORT="${GRPC_INGRESS_PORT:-5051}"
COMPOSE_FILE="${COMPOSE_FILE:-$REPO_ROOT/docker-compose.oss.yaml}"
ROWS="${ROWS:-50000}"

PYTHON_VENV="$PERF/.venv/bin/python"
[[ -x "$PYTHON_VENV" ]] || uv venv "$PERF/.venv"
uv pip install -r "$PERF/requirements.txt" --python "$PYTHON_VENV" -q

echo "[e7] enabling gRPC ingress on connector-worker (GRPC_INGRESS_PORT=$GRPC_INGRESS_PORT, flush=1min)"
GRPC_INGRESS_PORT="$GRPC_INGRESS_PORT" PARQUET_FLUSH_INTERVAL="${PARQUET_FLUSH_INTERVAL:-1}" \
  docker compose -f "$COMPOSE_FILE" up -d --force-recreate --no-deps building-os.connector-worker
sleep 12

"$PYTHON_VENV" "$PERF/measure_bytes_per_row.py" \
  --out "$OUT" --rows "$ROWS" \
  --ingress "localhost:${GRPC_INGRESS_PORT}" \
  --oxigraph "${OXIGRAPH_URL:-http://localhost:7878}" \
  --minio-container "${MINIO_CONTAINER:-building-os.minio}"
rc=$?
echo "[e7] bytes/row done → $OUT (rc=$rc)"
exit $rc
