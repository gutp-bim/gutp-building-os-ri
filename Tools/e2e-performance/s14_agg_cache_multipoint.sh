#!/usr/bin/env bash
# E4 残 KPI — 集計キャッシュヒット & multi-point スケーリング (#242). Enables the gRPC ingress + a short
# flush on the connector, then runs s14_agg_cache_multipoint.py.
#
# Usage: bash s14_agg_cache_multipoint.sh [OUT_DIR]   (GRPC_INGRESS_PORT 5051; FLUSH_INTERVAL_MIN 1)
set -euo pipefail

REPO_ROOT="$(git rev-parse --show-toplevel)"
PERF="$REPO_ROOT/Tools/e2e-performance"
OUT="${1:-$PERF/results/E4-cachemp-$(date +%Y%m%d-%H%M%S)}"
mkdir -p "$OUT"

export GRPC_INGRESS_PORT="${GRPC_INGRESS_PORT:-5051}"
COMPOSE_FILE="${COMPOSE_FILE:-$REPO_ROOT/docker-compose.oss.yaml}"
FLUSH_INTERVAL_MIN="${FLUSH_INTERVAL_MIN:-1}"

PYTHON_VENV="$PERF/.venv/bin/python"
[[ -x "$PYTHON_VENV" ]] || uv venv "$PERF/.venv"
uv pip install -r "$PERF/requirements.txt" --python "$PYTHON_VENV" -q

# Aggressive compaction so settled hours get their agg_hourly rollups quickly → agg_hour reads hit
# rollups (not aggregate-on-read). interval=1min / settle=0 / min-parts=2.
echo "[s14] connector-worker: GRPC_INGRESS_PORT=$GRPC_INGRESS_PORT PARQUET_FLUSH_INTERVAL=$FLUSH_INTERVAL_MIN" \
     "LAKE_COMPACTION_INTERVAL=1 LAKE_COMPACTION_SETTLE_MINUTES=0"
GRPC_INGRESS_PORT="$GRPC_INGRESS_PORT" PARQUET_FLUSH_INTERVAL="$FLUSH_INTERVAL_MIN" \
  PARQUET_FLUSH_MAX_ROWS="${PARQUET_FLUSH_MAX_ROWS:-50}" \
  LAKE_COMPACTION_INTERVAL=1 LAKE_COMPACTION_SETTLE_MINUTES=0 LAKE_COMPACTION_MIN_PARTS=2 \
  docker compose -f "$COMPOSE_FILE" up -d --force-recreate --no-deps building-os.connector-worker
sleep 12

# flush (1min) + ≥1 compaction cycle (1min) + margin → rollups ready before agg_hour read.
"$PYTHON_VENV" "$PERF/s14_agg_cache_multipoint.py" \
  --out "$OUT" --flush-wait "$(( FLUSH_INTERVAL_MIN * 60 + 90 ))" \
  --ingress "localhost:${GRPC_INGRESS_PORT}" \
  --base-url "${BASE_URL:-http://localhost:5000}" \
  --oxigraph "${OXIGRAPH_URL:-http://localhost:7878}"
rc=$?
echo "[s14] E4 cache/multipoint done → $OUT (rc=$rc)"
exit $rc
