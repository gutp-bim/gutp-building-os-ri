#!/usr/bin/env bash
# E2 — Ingest E2E latency / freshness (Epic #238). Brings the connector-worker up with the gRPC
# GatewayIngress listener AND a short Parquet flush interval (so a flush lands inside the run for the
# freshness measurement), then runs s11_ingest_latency.py.
#
# Usage: bash s11_ingest_latency.sh [OUT_DIR]
#   GRPC_INGRESS_PORT (5051), FRAMES (600), RATE (20), FLUSH_INTERVAL_MIN (1) overridable.
set -euo pipefail

REPO_ROOT="$(git rev-parse --show-toplevel)"
PERF="$REPO_ROOT/Tools/e2e-performance"
OUT="${1:-$PERF/results/E2-$(date +%Y%m%d-%H%M%S)}"
mkdir -p "$OUT"

export GRPC_INGRESS_PORT="${GRPC_INGRESS_PORT:-5051}"
COMPOSE_FILE="${COMPOSE_FILE:-$REPO_ROOT/docker-compose.oss.yaml}"
FRAMES="${FRAMES:-600}"
RATE="${RATE:-20}"
FLUSH_INTERVAL_MIN="${FLUSH_INTERVAL_MIN:-1}"

PYTHON_VENV="$PERF/.venv/bin/python"
[[ -x "$PYTHON_VENV" ]] || uv venv "$PERF/.venv"
uv pip install -r "$PERF/requirements.txt" --python "$PYTHON_VENV" -q

# Recreate the connector-worker with the gRPC ingress listener + a 1-min flush so freshness_lag gets a
# fresh sample during the run (PARQUET_FLUSH_INTERVAL is in minutes).
echo "[s11] connector-worker: GRPC_INGRESS_PORT=$GRPC_INGRESS_PORT PARQUET_FLUSH_INTERVAL=$FLUSH_INTERVAL_MIN"
GRPC_INGRESS_PORT="$GRPC_INGRESS_PORT" PARQUET_FLUSH_INTERVAL="$FLUSH_INTERVAL_MIN" \
  docker compose -f "$COMPOSE_FILE" up -d --force-recreate --no-deps building-os.connector-worker
sleep 12

"$PYTHON_VENV" "$PERF/s11_ingest_latency.py" \
  --out "$OUT" --frames "$FRAMES" --rate "$RATE" \
  --ingress "localhost:${GRPC_INGRESS_PORT}" \
  --flush-interval-min "$FLUSH_INTERVAL_MIN" \
  --nats "${NATS_URL:-nats://localhost:4222}" \
  --oxigraph "${OXIGRAPH_URL:-http://localhost:7878}" \
  --prometheus "${PROMETHEUS_URL:-http://localhost:9090}"
rc=$?

echo "[s11] E2 done → $OUT (rc=$rc)"
exit $rc
