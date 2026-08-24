#!/usr/bin/env bash
# E10 — 長時間ソーク試験 (#297 follow-up). OSS スタックを既定値（flush/compaction 間隔は
# アプリのデフォルトのまま）で起動し、s19_endurance_soak.py（gRPC 持続負荷 + RSS/consumer
# pending/health サンプリング）を DURATION_HOURS 時間走らせる。
#
# QUICK モードの s15/s16 と異なり、compaction の鋸歯パターンを実データで観測したいので
# PARQUET_FLUSH_INTERVAL 等は上書きしない（未設定ならアプリ既定値）。
#
# Usage: DURATION_HOURS=4 RATE=6 POINTS=1865 bash s19_endurance_soak.sh [OUT_DIR]
set -euo pipefail

REPO_ROOT="$(git rev-parse --show-toplevel)"
PERF="$REPO_ROOT/Tools/e2e-performance"
OUT="${1:-$PERF/results/E10-soak-$(date +%Y%m%d-%H%M%S)}"
mkdir -p "$OUT"

export GRPC_INGRESS_PORT="${GRPC_INGRESS_PORT:-5051}"
COMPOSE_FILE="${COMPOSE_FILE:-$REPO_ROOT/docker-compose.oss.yaml}"
DURATION_HOURS="${DURATION_HOURS:-4}"
RATE="${RATE:-6.2167}"
POINTS="${POINTS:-1865}"

PYTHON_VENV="$PERF/.venv/bin/python"
[[ -x "$PYTHON_VENV" ]] || uv venv "$PERF/.venv"
uv pip install -r "$PERF/requirements.txt" --python "$PYTHON_VENV" -q

echo "[s19] ensuring stack is up (GRPC_INGRESS_PORT=$GRPC_INGRESS_PORT, flush/compaction=app default)"
# building-os.api is not on the E10 ingest path (gRPC GatewayIngress -> connector-worker -> NATS ->
# Parquet writer bypasses it) and is intentionally excluded so the soak doesn't need its host port.
GRPC_INGRESS_PORT="$GRPC_INGRESS_PORT" docker compose -f "$COMPOSE_FILE" up -d \
  building-os.nats building-os.oxigraph building-os.minio \
  building-os.postgres building-os.pgbouncer building-os.pgbouncer-session \
  building-os.connector-worker building-os.gateway-bridge
sleep 15

echo "[s19] soak: ${DURATION_HOURS}h @ ${RATE}/s, ${POINTS} points -> $OUT"
"$PYTHON_VENV" "$PERF/s19_endurance_soak.py" \
  --out "$OUT" --duration-hours "$DURATION_HOURS" --rate "$RATE" --points "$POINTS" \
  --ingress "localhost:${GRPC_INGRESS_PORT}" \
  --oxigraph "${OXIGRAPH_URL:-http://localhost:7878}" \
  --minio-endpoint "${MINIO_ENDPOINT_HOST:-localhost:9000}"
rc=$?
echo "[s19] E10 soak done → $OUT (rc=$rc)"
exit $rc
