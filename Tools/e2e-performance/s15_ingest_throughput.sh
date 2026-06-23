#!/usr/bin/env bash
# E1 — gRPC GatewayIngress 持続スループット (#239). Enables gRPC ingress + short flush on the connector,
# then runs s15_ingest_throughput.py (sustained load → quality_checker → E1 gate metrics).
#
# Usage: bash s15_ingest_throughput.sh [OUT_DIR]   (RATE 200, DURATION 30; GRPC_INGRESS_PORT 5051)
set -euo pipefail

REPO_ROOT="$(git rev-parse --show-toplevel)"
PERF="$REPO_ROOT/Tools/e2e-performance"
OUT="${1:-$PERF/results/E1-throughput-$(date +%Y%m%d-%H%M%S)}"
mkdir -p "$OUT"

export GRPC_INGRESS_PORT="${GRPC_INGRESS_PORT:-5051}"
COMPOSE_FILE="${COMPOSE_FILE:-$REPO_ROOT/docker-compose.oss.yaml}"
RATE="${RATE:-200}"; DURATION="${DURATION:-30}"

PYTHON_VENV="$PERF/.venv/bin/python"
[[ -x "$PYTHON_VENV" ]] || uv venv "$PERF/.venv"
uv pip install -r "$PERF/requirements.txt" --python "$PYTHON_VENV" -q

echo "[s15] connector-worker: GRPC_INGRESS_PORT=$GRPC_INGRESS_PORT PARQUET_FLUSH_INTERVAL=1"
GRPC_INGRESS_PORT="$GRPC_INGRESS_PORT" PARQUET_FLUSH_INTERVAL="${PARQUET_FLUSH_INTERVAL:-1}" \
  docker compose -f "$COMPOSE_FILE" up -d --force-recreate --no-deps building-os.connector-worker
sleep 12

"$PYTHON_VENV" "$PERF/s15_ingest_throughput.py" \
  --out "$OUT" --rate "$RATE" --duration "$DURATION" \
  --ingress "localhost:${GRPC_INGRESS_PORT}" \
  --oxigraph "${OXIGRAPH_URL:-http://localhost:7878}" \
  --minio-endpoint "${MINIO_ENDPOINT_HOST:-localhost:9000}"
rc=$?
echo "[s15] E1 throughput done → $OUT (rc=$rc)"
exit $rc
