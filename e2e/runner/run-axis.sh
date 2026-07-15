#!/usr/bin/env bash
# 単一評価軸（E1–E8）を実行。既存 Tools/e2e-performance/ のスクリプトへ委譲し、未実装の軸は
# gap.json を残してスキップ（exit 0）。結果は --out で指定された run ディレクトリに集約。
#
# Usage: bash e2e/runner/run-axis.sh E3 --scale medium --out e2e/results/<run-id>
set -euo pipefail

AXIS="${1:?axis required (E1..E8)}"; shift || true
SCALE="medium"; OUT=""
while [[ $# -gt 0 ]]; do
  case "$1" in
    --scale) SCALE="$2"; shift 2 ;;
    --out)   OUT="$2"; shift 2 ;;
    *) shift ;;
  esac
done

REPO_ROOT="$(git rev-parse --show-toplevel)"
PERF="$REPO_ROOT/Tools/e2e-performance"
OUT="${OUT:-$REPO_ROOT/e2e/results/adhoc-$AXIS}"
mkdir -p "$OUT"

# QUICK モードは small スケール相当（既存スクリプトの慣習）。
[[ "$SCALE" == "small" ]] && export QUICK=true

gap() {  # 未実装の軸を記録してスキップ
  echo "[run-axis] $AXIS: GAP — $1"
  printf '{"axis":"%s","status":"gap","reason":"%s"}\n' "$AXIS" "$1" > "$OUT/$AXIS.gap.json"
  exit 0
}

# Read-path k6 axes (E3/E4) need points that exist in BOTH the twin and the lake. Seed the synthetic
# load-gen points into the twin (read path 404s for unknown points) and export POINT_IDS for k6.
# SEED_RUN8 selects which run's points (default: the most recent perf run in the lake; override via env).
PYTHON_VENV="$PERF/.venv/bin/python"
seed_read_points() {
  command -v k6 >/dev/null || return 1
  # Respect a caller-provided POINT_IDS (e.g. a persistent dataset already in the lake) — only seed
  # synthetic points when none was supplied.
  if [[ -n "${POINT_IDS:-}" ]]; then
    export RANGE_LOOKBACK_HOURS="${RANGE_LOOKBACK_HOURS:-24}"
    echo "[run-axis] using caller-provided POINT_IDS ($(echo "$POINT_IDS" | tr ',' '\n' | wc -l) ids), skipping seed"
    return 0
  fi
  [[ -f "$PYTHON_VENV" ]] || uv venv "$PERF/.venv" >/dev/null 2>&1
  uv pip install -r "$PERF/requirements.txt" --python "$PYTHON_VENV" -q 2>/dev/null || true
  local run8="${SEED_RUN8:-20260615}"
  POINT_IDS="$("$PYTHON_VENV" "$PERF/seed_twin_points.py" --run-id "$run8" --devices 10 --points-per-device 10 2>/dev/null | tail -1)"
  export POINT_IDS
  # Existing lake data may be older than 1h; widen the k6 range lookback so queries cover it.
  export RANGE_LOOKBACK_HOURS="${RANGE_LOOKBACK_HOURS:-24}"
  echo "[run-axis] seeded read points (run8=$run8); POINT_IDS set, RANGE_LOOKBACK_HOURS=$RANGE_LOOKBACK_HOURS"
}

# Normalize a k6 --summary-export JSON into the gate's canonical {axis, metrics} shape (E3/E4/E6).
NORM_PYTHON="$PYTHON_VENV"; [[ -x "$NORM_PYTHON" ]] || NORM_PYTHON="python3"
normalize_k6() {  # <axis> <k6-summary-json>
  "$NORM_PYTHON" "$REPO_ROOT/e2e/runner/normalize_k6.py" --axis "$1" --summary "$2" --out "$OUT" || true
}

case "$AXIS" in
  E1) command -v docker >/dev/null || gap "docker 未導入（gRPC ingress を起動できない）"
      # Primary: gRPC GatewayIngress 持続スループット負荷（#239）→ {axis:E1, metrics} を直接出力し gate
      # がそのまま評価。MQTT 経路の s2/s3 は補助計測として併走。
      RATE="${RATE:-200}" DURATION="${DURATION:-30}" bash "$PERF/s15_ingest_throughput.sh" "$OUT/E1-throughput" || true
      bash "$PERF/s3_burst.sh" "$OUT/$AXIS-burst" || true ;;
  E2) command -v docker >/dev/null || gap "docker 未導入（gRPC ingress を起動できない）"
      FRAMES="${FRAMES:-600}" RATE="${RATE:-20}" bash "$PERF/s11_ingest_latency.sh" "$OUT/$AXIS" || true ;;
  E3) command -v k6 >/dev/null || gap "k6 未導入"
      seed_read_points
      SKIP_LATEST="${SKIP_LATEST:-false}" k6 run "$PERF/k6/s5_api_read.js"  | tee "$OUT/$AXIS-s5.txt" || true
      k6 run --summary-export="$OUT/$AXIS-s9.summary.json" "$PERF/k6/s9_warm_kpi.js" | tee "$OUT/$AXIS-s9.txt" || true
      normalize_k6 E3 "$OUT/$AXIS-s9.summary.json"
      # Freshness / stale-ratio (#241): event → Hot KV → latest API. Writes its own
      # {axis:E3_latest_value, metrics} JSON, merged with latest_api_p95 by gate.py.
      command -v docker >/dev/null && bash "$PERF/s13_latest_freshness.sh" "$OUT/E3-freshness" || true ;;
  E4) command -v k6 >/dev/null || gap "k6 未導入"
      seed_read_points
      k6 run --summary-export="$OUT/$AXIS-s9.summary.json" "$PERF/k6/s9_warm_kpi.js" | tee "$OUT/$AXIS-s9.txt" || true
      normalize_k6 E4 "$OUT/$AXIS-s9.summary.json"
      # 残 KPI (#242): agg cache-hit + multipoint sublinear. {axis:E4_historical_query, metrics} を
      # 出力し gate が s9 の warm/cold/agg_hour とマージ。
      command -v docker >/dev/null && bash "$PERF/s14_agg_cache_multipoint.sh" "$OUT/E4-cachemp" || true ;;
  E5) command -v docker >/dev/null || gap "docker 未導入（gRPC ingress を起動できない）"
      FRAMES="${FRAMES:-200}" bash "$PERF/s10_pointlist_integrity.sh" "$OUT/$AXIS" || true ;;
  E6) E6_RUN_ID="e6-$(date +%s)"
      # s6 takes its run id as $1 and writes results/<run-id>/k6-summary.json; pass the id (not a path)
      # so the normalizer can find the summary (command_rtt).
      bash "$PERF/s6_point_control.sh" "$E6_RUN_ID" || true
      normalize_k6 E6 "$PERF/results/$E6_RUN_ID/k6-summary.json"
      # Safety scenarios (#244): not-writable / offline→503 / typed-failure / stale-replay. Writes its
      # own {axis:E6_control_safety, metrics} JSON, merged with the rtt metric by gate.py.
      command -v docker >/dev/null && bash "$PERF/s12_control_safety.sh" "$OUT/E6-safety" || true ;;
  E7) bash "$PERF/measure_lake_storage.sh"  | tee "$OUT/$AXIS-lake.txt" || true
      bash "$PERF/measure_compression.sh"   | tee "$OUT/$AXIS-compress.txt" || true
      "$NORM_PYTHON" "$REPO_ROOT/e2e/runner/normalize_storage.py" --out "$OUT" || true
      # bytes/row vs TimescaleDB 非圧縮 (#245): {axis:E7_storage_cost, metrics} を出力し gate がマージ。
      command -v docker >/dev/null && bash "$PERF/measure_bytes_per_row.sh" "$OUT/E7-bytesrow" || true ;;
  E8) command -v docker >/dev/null || gap "docker 未導入（障害注入できない）"
      # Primary: connector 停止→再起動の RTO + 復旧後データ損失（#246）。{axis:E8, metrics} を直接出力。
      PHASE2="${PHASE2:-2000}" bash "$PERF/s16_resilience_rto.sh" "$OUT/E8-resilience" || true ;;
  E9) command -v yarn >/dev/null || gap "yarn 未導入（web-client Playwright を実行できない）"
      # 運用者ユーザビリティ（#159）: web-client の Playwright(route-mock) が axe/鮮度表示時間などを
      # 計測して {axis:E9_operator_usability, metrics} を $OUT/E9.json に出力（docker 不要）。
      # ブラウザは CI では `npx playwright install chromium`、preinstall 環境では
      # PLAYWRIGHT_CHROMIUM_EXECUTABLE で指定（web-client/playwright.config.ts 参照）。
      ( cd "$REPO_ROOT/web-client" && E9_OUT="$OUT/E9.json" yarn test:e2e e9-metrics ) || true ;;
  *)  echo "[run-axis] unknown axis: $AXIS" >&2; exit 2 ;;
esac

echo "[run-axis] $AXIS done → $OUT"
