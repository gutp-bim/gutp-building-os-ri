#!/usr/bin/env bash
# E5 — Point List / Digital Twin 整合性 (#243). Enables the gRPC GatewayIngress listener on the
# connector-worker (GRPC_INGRESS_PORT), then drives s10_pointlist_integrity.py against it.
#
# Usage: bash s10_pointlist_integrity.sh [OUT_DIR]
#   GRPC_INGRESS_PORT (default 5051), FRAMES (default 200), COMPOSE_FILE, BASE_URL, OXIGRAPH_URL override.
set -euo pipefail

REPO_ROOT="$(git rev-parse --show-toplevel)"
PERF="$REPO_ROOT/Tools/e2e-performance"
OUT="${1:-$PERF/results/E5-$(date +%Y%m%d-%H%M%S)}"
mkdir -p "$OUT"

export GRPC_INGRESS_PORT="${GRPC_INGRESS_PORT:-5051}"
COMPOSE_FILE="${COMPOSE_FILE:-$REPO_ROOT/docker-compose.oss.yaml}"
FRAMES="${FRAMES:-200}"

PYTHON_VENV="$PERF/.venv/bin/python"
[[ -x "$PYTHON_VENV" ]] || uv venv "$PERF/.venv"
uv pip install -r "$PERF/requirements.txt" --python "$PYTHON_VENV" -q

# Bring the connector-worker up WITH the gRPC ingress listener bound (recreate to apply env+port).
echo "[s10] enabling gRPC ingress on connector-worker (GRPC_INGRESS_PORT=$GRPC_INGRESS_PORT)"
GRPC_INGRESS_PORT="$GRPC_INGRESS_PORT" docker compose -f "$COMPOSE_FILE" up -d --force-recreate \
  --no-deps building-os.connector-worker
sleep 12  # NATS reconnect + metadata cache + Kestrel h2c listener

"$PYTHON_VENV" "$PERF/s10_pointlist_integrity.py" \
  --out "$OUT" \
  --frames "$FRAMES" \
  --ingress "localhost:${GRPC_INGRESS_PORT}" \
  --compose-file "$COMPOSE_FILE" \
  --base-url "${BASE_URL:-http://localhost:5000}" \
  --oxigraph "${OXIGRAPH_URL:-http://localhost:7878}"
rc=$?

echo "[s10] E5 done → $OUT (rc=$rc)"
exit $rc
