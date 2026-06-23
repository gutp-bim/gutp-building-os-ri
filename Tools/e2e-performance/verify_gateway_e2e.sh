#!/usr/bin/env bash
# E2E gateway verification — nexus-gateway point_list.csv を正本とする検証スクリプト.
#
# 使い方:
#   bash verify_gateway_e2e.sh --csv /path/to/point_list.csv [OPTIONS]
#
# オプション:
#   --csv PATH          nexus-gateway の point_list.csv (必須)
#   --skip-seed         OxiGraph への seed をスキップ
#   --cleanup           終了後に seed したポイントを削除
#   --with-ingress      ConnectorWorker の gRPC ingress listener を起動して Phase 2 を実行
#   --with-control      Phase 3 (制御分類テスト) を実行
#   --out DIR           結果出力ディレクトリ (default: Tools/e2e-performance/results/gateway-e2e-<ts>)
#
# 環境変数:
#   GRPC_INGRESS_PORT   gRPC ingress ポート (default: 5051)
#   COMPOSE_FILE        docker-compose ファイル
#   BASE_URL            API Server URL (default: http://localhost:5000)
#   OXIGRAPH_URL        OxiGraph URL (default: http://localhost:7878)
#
# 例:
#   # nexus-gateway と Building OS が両方起動している状態でフルE2E実行
#   NEXUS_CSV=../nexus-gateway/fixtures/integration/point_list.csv
#   bash Tools/e2e-performance/verify_gateway_e2e.sh --csv "$NEXUS_CSV" --with-ingress --with-control
#
#   # ポイントリスト API のみ検証 (事前に seed 済み)
#   bash Tools/e2e-performance/verify_gateway_e2e.sh --csv "$NEXUS_CSV" --skip-seed
set -euo pipefail

REPO_ROOT="$(git rev-parse --show-toplevel)"
PERF="$REPO_ROOT/Tools/e2e-performance"

CSV_PATH=""
SKIP_SEED=""
CLEANUP_FLAG=""
WITH_INGRESS=""
WITH_CONTROL=""
OUT=""

while [[ $# -gt 0 ]]; do
  case "$1" in
    --csv)      CSV_PATH="$2"; shift 2 ;;
    --skip-seed) SKIP_SEED="--skip-seed"; shift ;;
    --cleanup)  CLEANUP_FLAG="--cleanup"; shift ;;
    --with-ingress) WITH_INGRESS="1"; shift ;;
    --with-control) WITH_CONTROL="--control-check"; shift ;;
    --out)      OUT="$2"; shift 2 ;;
    *) echo "Unknown option: $1" >&2; exit 1 ;;
  esac
done

if [[ -z "$CSV_PATH" ]]; then
  # 引数未指定の場合、nexus-gateway が兄弟ディレクトリにある想定でデフォルトを試みる
  DEFAULT_CSV="$(dirname "$REPO_ROOT")/nexus-gateway/fixtures/integration/point_list.csv"
  if [[ -f "$DEFAULT_CSV" ]]; then
    CSV_PATH="$DEFAULT_CSV"
    echo "[info] --csv 未指定: $DEFAULT_CSV を使用"
  else
    echo "Error: --csv PATH を指定してください" >&2
    echo "  例: bash verify_gateway_e2e.sh --csv /path/to/point_list.csv" >&2
    exit 1
  fi
fi

if [[ ! -f "$CSV_PATH" ]]; then
  echo "Error: CSV ファイルが見つかりません: $CSV_PATH" >&2
  exit 1
fi

TS="$(date +%Y%m%d-%H%M%S)"
OUT="${OUT:-$PERF/results/gateway-e2e-$TS}"
mkdir -p "$OUT"

GRPC_INGRESS_PORT="${GRPC_INGRESS_PORT:-5051}"
COMPOSE_FILE="${COMPOSE_FILE:-$REPO_ROOT/docker-compose.oss.yaml}"
BASE_URL="${BASE_URL:-http://localhost:5000}"
OXIGRAPH_URL="${OXIGRAPH_URL:-http://localhost:7878}"

# venv のセットアップ
PYTHON_VENV="$PERF/.venv/bin/python"
if [[ ! -x "$PYTHON_VENV" ]]; then
  uv venv "$PERF/.venv"
fi
uv pip install -r "$PERF/requirements.txt" --python "$PYTHON_VENV" -q

echo "[info] CSV: $CSV_PATH"
echo "[info] 出力: $OUT"

# seed を先に実行して競合を解消してから connector-worker を起動する
INGRESS_ARG=""
if [[ -n "$WITH_INGRESS" ]]; then
  # 先に OxiGraph seed（競合削除 → 挿入）を実行
  if [[ -z "$SKIP_SEED" ]]; then
    echo "[info] Seeding OxiGraph from CSV (先行実行)..."
    "$PYTHON_VENV" "$PERF/seed_from_csv.py" --csv "$CSV_PATH" --oxigraph "$OXIGRAPH_URL" > /dev/null
    SKIP_SEED="--skip-seed"
  fi
  echo "[info] Phase 2 gRPC ingress: connector-worker を GRPC_INGRESS_PORT=$GRPC_INGRESS_PORT で再起動"
  GRPC_INGRESS_PORT="$GRPC_INGRESS_PORT" docker compose -f "$COMPOSE_FILE" up -d \
    --force-recreate --no-deps building-os.connector-worker
  echo "[info] 起動待機 (12s)..."
  sleep 12
  INGRESS_ARG="--ingress localhost:${GRPC_INGRESS_PORT}"
fi

"$PYTHON_VENV" "$PERF/verify_gateway_e2e.py" \
  --csv "$CSV_PATH" \
  --oxigraph "$OXIGRAPH_URL" \
  --base-url "$BASE_URL" \
  --out "$OUT" \
  $INGRESS_ARG \
  $SKIP_SEED \
  $CLEANUP_FLAG \
  $WITH_CONTROL

RC=$?
echo "[info] 完了 → $OUT/gateway-e2e.json (rc=$RC)"
exit $RC
