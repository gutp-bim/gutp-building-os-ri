#!/usr/bin/env bash
# E8 — 障害復旧 + RTO (#246). Enables gRPC ingress + short flush on the connector, then runs
# s16_resilience_rto.py (stop/start connector → RTO + post-recovery data-loss).
#
# Usage: bash s16_resilience_rto.sh [OUT_DIR]   (PHASE2 default 2000; GRPC_INGRESS_PORT 5051)
set -euo pipefail

REPO_ROOT="$(git rev-parse --show-toplevel)"
PERF="$REPO_ROOT/Tools/e2e-performance"
OUT="${1:-$PERF/results/E8-resilience-$(date +%Y%m%d-%H%M%S)}"
mkdir -p "$OUT"

export GRPC_INGRESS_PORT="${GRPC_INGRESS_PORT:-5051}"
COMPOSE_FILE="${COMPOSE_FILE:-$REPO_ROOT/docker-compose.oss.yaml}"
PHASE2="${PHASE2:-2000}"

PYTHON_VENV="$PERF/.venv/bin/python"
[[ -x "$PYTHON_VENV" ]] || uv venv "$PERF/.venv"
uv pip install -r "$PERF/requirements.txt" --python "$PYTHON_VENV" -q

echo "[s16] connector-worker: GRPC_INGRESS_PORT=$GRPC_INGRESS_PORT PARQUET_FLUSH_INTERVAL=1"
GRPC_INGRESS_PORT="$GRPC_INGRESS_PORT" PARQUET_FLUSH_INTERVAL="${PARQUET_FLUSH_INTERVAL:-1}" \
  docker compose -f "$COMPOSE_FILE" up -d --force-recreate --no-deps building-os.connector-worker
sleep 12

"$PYTHON_VENV" "$PERF/s16_resilience_rto.py" \
  --out "$OUT" --phase2 "$PHASE2" \
  --container building-os.connector-worker \
  --ingress "localhost:${GRPC_INGRESS_PORT}" \
  --oxigraph "${OXIGRAPH_URL:-http://localhost:7878}" \
  --minio-endpoint "${MINIO_ENDPOINT_HOST:-localhost:9000}"
rc=$?
echo "[s16] E8 resilience done → $OUT (rc=$rc)"
exit $rc
