#!/usr/bin/env bash
# E11 — lake retention/compaction under sustained multi-building load (#263). CAPPED-RUN variant:
# a short retention window + fast flush/compaction cadence so the whole run completes in well under
# 10 minutes (same precedent E10 set with its own capped-run variant — see
# e2e/scenarios/E11-lake-retention-scale.md).
#
# Brings the OSS stack up with a fast flush/compaction cadence (so a short run can actually see
# multiple flush cycles and a compaction pass) and a short LAKE_RETENTION_DAYS (so the ILM rule the
# harness verifies is meaningfully small), then runs s20_retention_compaction.py.
#
# Usage: bash s20_retention_compaction.sh [OUT_DIR]
#        POINTS=1000 BUILDINGS=5 GATEWAYS=10 WAVES=4 bash s20_retention_compaction.sh [OUT_DIR]
#        RETENTION_DAYS=2 bash s20_retention_compaction.sh [OUT_DIR]
set -euo pipefail

REPO_ROOT="$(git rev-parse --show-toplevel)"
PERF="$REPO_ROOT/Tools/e2e-performance"
OUT="${1:-$PERF/results/E11-retention-$(date +%Y%m%d-%H%M%S)}"
mkdir -p "$OUT"

export GRPC_INGRESS_PORT="${GRPC_INGRESS_PORT:-5051}"
COMPOSE_FILE="${COMPOSE_FILE:-$REPO_ROOT/docker-compose.oss.yaml}"

POINTS="${POINTS:-300}"
BUILDINGS="${BUILDINGS:-3}"
GATEWAYS="${GATEWAYS:-6}"
WAVES="${WAVES:-3}"
RETENTION_DAYS="${RETENTION_DAYS:-1}"

# Fast cadence so a <10-minute run sees multiple flush cycles and a compaction pass without waiting
# out the app defaults (flush 5min / compaction scan 15min / settle grace 30min). SETTLE_MINUTES=0
# is safe here because the harness places data in an ALREADY wall-clock-settled hour
# (settled_target_hour, default 3h back) — it does not depend on a real settle grace to fire fast.
export PARQUET_FLUSH_INTERVAL="${PARQUET_FLUSH_INTERVAL:-1}"
export LAKE_COMPACTION_INTERVAL="${LAKE_COMPACTION_INTERVAL:-1}"
export LAKE_COMPACTION_SETTLE_MINUTES="${LAKE_COMPACTION_SETTLE_MINUTES:-0}"
export LAKE_RETENTION_DAYS="${LAKE_RETENTION_DAYS:-$RETENTION_DAYS}"

PYTHON_VENV="$PERF/.venv/bin/python"
[[ -x "$PYTHON_VENV" ]] || uv venv "$PERF/.venv"
uv pip install -r "$PERF/requirements.txt" --python "$PYTHON_VENV" -q

echo "[s20] ensuring stack is up (GRPC_INGRESS_PORT=$GRPC_INGRESS_PORT, flush=${PARQUET_FLUSH_INTERVAL}min,"
echo "[s20]   compaction=${LAKE_COMPACTION_INTERVAL}min/settle=${LAKE_COMPACTION_SETTLE_MINUTES}min,"
echo "[s20]   retention=${LAKE_RETENTION_DAYS}d)"
# building-os.api is not on this axis's ingest path (gRPC GatewayIngress -> connector-worker -> NATS
# -> Parquet writer bypasses it), same exclusion s19_endurance_soak.sh makes for E10.
docker compose -f "$COMPOSE_FILE" up -d \
  building-os.nats building-os.oxigraph building-os.minio \
  building-os.postgres building-os.pgbouncer building-os.pgbouncer-session \
  building-os.connector-worker building-os.gateway-bridge
sleep 15

echo "[s20] retention/compaction run: ${POINTS} points / ${BUILDINGS} buildings / ${GATEWAYS} gateways / ${WAVES} waves -> $OUT"
rc=0
"$PYTHON_VENV" "$PERF/s20_retention_compaction.py" \
  --out "$OUT" --points "$POINTS" --buildings "$BUILDINGS" --gateways "$GATEWAYS" --waves "$WAVES" \
  --retention-days "$RETENTION_DAYS" \
  --ingress "localhost:${GRPC_INGRESS_PORT}" \
  --oxigraph "${OXIGRAPH_URL:-http://localhost:7878}" \
  --minio-endpoint "${MINIO_ENDPOINT_HOST:-localhost:9000}" || rc=$?
echo "[s20] E11 retention/compaction done → $OUT (rc=$rc)"
exit $rc
