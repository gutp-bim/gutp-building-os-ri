#!/usr/bin/env bash
# Building OS E2E 評価オーケストレーション。
# 評価軸 E1–E8 を順に実行し、e2e/results/<run-id>/ に結果を集約する。
# 既存の Tools/e2e-performance/ スクリプトを再利用し、未実装の軸はギャップとしてスキップ＋記録する。
#
# Usage:
#   bash e2e/runner/run-all.sh
#   SCALE=large bash e2e/runner/run-all.sh
#   ONLY=E3,E4 bash e2e/runner/run-all.sh
#   RUN_ID=20260614T020000Z-medium bash e2e/runner/run-all.sh
#
# 前提: docker compose -f docker-compose.oss.yaml up -d 済み。詳細は e2e/README.md。
set -euo pipefail

REPO_ROOT="$(git rev-parse --show-toplevel)"
PERF_DIR="$REPO_ROOT/Tools/e2e-performance"
SCALE="${SCALE:-medium}"
RUN_ID="${RUN_ID:-$(date -u +%Y%m%dT%H%M%SZ)-${SCALE}}"
OUT_DIR="$REPO_ROOT/e2e/results/$RUN_ID"
ONLY="${ONLY:-E1,E2,E3,E4,E5,E6,E7,E8,E9}"

mkdir -p "$OUT_DIR"
echo "[run-all] run_id=$RUN_ID scale=$SCALE only=$ONLY"
echo "[run-all] output=$OUT_DIR"

# 環境スナップショット（再現性）。
{
  echo "{"
  echo "  \"run_id\": \"$RUN_ID\","
  echo "  \"scale\": \"$SCALE\","
  echo "  \"git_sha\": \"$(git rev-parse HEAD)\","
  echo "  \"timestamp\": \"$(date -u +%Y-%m-%dT%H:%M:%SZ)\","
  echo "  \"warm_store\": \"${WARM_STORE:-parquet}\""
  echo "}"
} > "$OUT_DIR/env.json"

want() { [[ ",$ONLY," == *",$1,"* ]]; }
run_axis() { bash "$REPO_ROOT/e2e/runner/run-axis.sh" "$1" --scale "$SCALE" --out "$OUT_DIR"; }

for axis in E1 E2 E3 E4 E5 E6 E7 E8 E9; do
  if want "$axis"; then
    echo "[run-all] === $axis ==="
    run_axis "$axis" || echo "[run-all] $axis exited non-zero (recorded)"
  fi
done

# KPI ゲート: 各軸の結果 JSON を e2e/kpi-thresholds.yaml と突合し pass/fail レポートを生成する。
# 比較可能な KPI が 1 つでも FAIL なら run-all 自体も非ゼロ終了（CI/論文のゲート）。
GATE_PY="$REPO_ROOT/e2e/runner/gate.py"
GATE_PYTHON="$PERF_DIR/.venv/bin/python"
[[ -x "$GATE_PYTHON" ]] || GATE_PYTHON="python3"
echo "[run-all] === KPI gate ==="
gate_rc=0
"$GATE_PYTHON" "$GATE_PY" "$OUT_DIR" || gate_rc=$?
echo "[run-all] done. レポート: $OUT_DIR/kpi-report.md / $OUT_DIR/gate.json"
exit $gate_rc
