#!/usr/bin/env bash
# E10 — 長時間ソーク試験 (#297 follow-up). OSS スタックを既定値（flush/compaction 間隔は
# アプリのデフォルトのまま）で起動し、s19_endurance_soak.py（gRPC 持続負荷 + RSS/consumer
# pending/health サンプリング）を DURATION_HOURS 時間走らせる。
#
# QUICK モードの s15/s16 と異なり、compaction の鋸歯パターンを実データで観測したいので
# PARQUET_FLUSH_INTERVAL 等は上書きしない（未設定ならアプリ既定値）。
#
# #370 の調査用に 2 つのノブがある（どちらも未設定なら従来どおりの挙動）:
#   CHUNK_SECONDS=0   gRPC ストリームを張り直さず run 全体を 1 本で流す（既定は .py 側の 300s）。
#                     #297 の単一 24h ストリームと同条件にするための A/B 対照。
#   PROMETHEUS_URL=…  .NET runtime メトリクス（GC heap/committed/LOH/POH/thread pool）を Prometheus
#                     から併せてサンプリングする。指定時のみ observability profile を起動する。
#                     **被測定系の構成が変わる**（下の注記参照）ので A/B の 2 本は必ず揃えること。
#
# Usage: DURATION_HOURS=4 RATE=6 POINTS=1865 bash s19_endurance_soak.sh [OUT_DIR]
#        CHUNK_SECONDS=0 PROMETHEUS_URL=http://localhost:9090 bash s19_endurance_soak.sh [OUT_DIR]
#        MEM_LIMIT=768m bash s19_endurance_soak.sh [OUT_DIR]   # #297: survival under a cap
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
# CHUNK_SECONDS / PROMETHEUS_URL はここでは既定値を持たない — 未設定なら .py 側の既定
# （--chunk-seconds 300 / runtime サンプリング無効）がそのまま唯一の正本になる。
CHUNK_SECONDS="${CHUNK_SECONDS:-}"
PROMETHEUS_URL="${PROMETHEUS_URL:-}"
# MEM_LIMIT: cap the connector worker (#297). Unset = no cap, which is the historical behaviour.
# The 24h A/B established that uncapped RSS is a property of how much memory the GC sees, so the
# question worth asking of a longer run is "does it live within a limit", not "where does it stop".
MEM_LIMIT="${MEM_LIMIT:-}"

PYTHON_VENV="$PERF/.venv/bin/python"
[[ -x "$PYTHON_VENV" ]] || uv venv "$PERF/.venv"
uv pip install -r "$PERF/requirements.txt" --python "$PYTHON_VENV" -q

# Compose files as an array: the mem-limit variant adds a second -f, and a single COMPOSE_FILE
# string cannot carry that.
COMPOSE_ARGS=(-f "$COMPOSE_FILE")
if [[ -n "$MEM_LIMIT" ]]; then
  COMPOSE_ARGS+=(-f "$REPO_ROOT/docker-compose.memlimit.yaml")
  export CONNECTOR_WORKER_MEM_LIMIT="$MEM_LIMIT"
  echo "[s19] connector-worker capped at $MEM_LIMIT (#297 survival-under-a-cap variant)"
  echo "[s19]   oom_count_total / restart_count_total already gate at 0, so a cap the process"
  echo "[s19]   cannot live within fails the run. Watch gc_collections_growth_per_hour for the"
  echo "[s19]   cost of a smaller nursery, and the ingest KPIs for whether it reached the data path."
fi

echo "[s19] ensuring stack is up (GRPC_INGRESS_PORT=$GRPC_INGRESS_PORT, flush/compaction=app default)"
# building-os.api is not on the E10 ingest path (gRPC GatewayIngress -> connector-worker -> NATS ->
# Parquet writer bypasses it) and is intentionally excluded so the soak doesn't need its host port.
GRPC_INGRESS_PORT="$GRPC_INGRESS_PORT" docker compose "${COMPOSE_ARGS[@]}" up -d \
  building-os.nats building-os.oxigraph building-os.minio \
  building-os.postgres building-os.pgbouncer building-os.pgbouncer-session \
  building-os.connector-worker building-os.gateway-bridge

EXTRA_ARGS=()
if [[ -n "$CHUNK_SECONDS" ]]; then
  EXTRA_ARGS+=(--chunk-seconds "$CHUNK_SECONDS")
fi
if [[ -n "$PROMETHEUS_URL" ]]; then
  # Prometheus / otel-collector は `profiles: [observability]` なので既定スタックには居ない。
  # connector-worker 側は OTEL_EXPORTER_OTLP_ENDPOINT が常に設定済み（宛先が無いときは no-op）
  # なので、被測定系のコードも設定も変わらず「exporter の宛先が生きるかどうか」だけが変わる。
  #
  # ただし blast radius は Prometheus + collector の 2 つでは済まない: compose の
  # `building-os.otel-collector` は tempo / loki に depends_on しているので、下のコマンドは
  # **tempo と loki も起動する**（同 profile なので条件も満たす）。その結果 collector の logs
  # パイプラインの宛先が生き、connector-worker の OTLP ログ出力が実際に送られるようになる —
  # つまり #370 で見ている当のプロセスのアロケーションが（僅かとはいえ）増える。加えて同一ホスト上
  # のコンテナが 4 つ増え、CPU / メモリ / page cache を食う。
  # → **PROMETHEUS_URL 有りの run の RSS 値は、無しの run（#370 の baseline `soak-20260823223433`
  #    を含む）と直接比較してはいけない。** A/B の 2 本は必ず両方 ON か両方 OFF で回すこと。
  # 起動するサービスは省略せず全部書く（意図せぬ 2 つが黙って上がるより、名前が並んでいる方がよい）。
  echo "[s19] PROMETHEUS_URL=$PROMETHEUS_URL — starting the observability profile (#370 runtime metrics)"
  echo "[s19]   note: this also starts tempo/loki (otel-collector depends_on) and makes the OTLP"
  echo "[s19]   log pipeline live — Prometheus-on RSS is not comparable to Prometheus-off runs"
  docker compose "${COMPOSE_ARGS[@]}" --profile observability up -d \
    building-os.prometheus building-os.tempo building-os.loki building-os.otel-collector
  EXTRA_ARGS+=(--prometheus "$PROMETHEUS_URL")
fi
sleep 15

if [[ -n "$PROMETHEUS_URL" ]]; then
  # Prometheus は永続ボリューム（prometheus_data、15d 保持）の TSDB を WAL リプレイしている間
  # /api/v1/* に 503 を返す。過去 run のデータ量次第で共通の sleep 15 では足りず、そこで s19 の
  # 系列名解決が 503 を掴むと「数時間走ったのに runtime 列が空の run」になる（.py 側にも最初の
  # 15 分だけ再解決するリトライを入れてあるが、そもそも待てるならここで待つのが素直）。
  PROM_READY_TIMEOUT="${PROM_READY_TIMEOUT:-180}"
  echo "[s19] waiting for Prometheus to be ready (max ${PROM_READY_TIMEOUT}s)"
  prom_deadline=$((SECONDS + PROM_READY_TIMEOUT))
  until curl -fsS "${PROMETHEUS_URL%/}/-/ready" >/dev/null 2>&1; do
    if (( SECONDS >= prom_deadline )); then
      echo "[s19] WARNING: Prometheus still not ready after ${PROM_READY_TIMEOUT}s — runtime metrics may be missing" >&2
      break
    fi
    sleep 5
  done
fi

echo "[s19] soak: ${DURATION_HOURS}h @ ${RATE}/s, ${POINTS} points -> $OUT"
rc=0
"$PYTHON_VENV" "$PERF/s19_endurance_soak.py" \
  --out "$OUT" --duration-hours "$DURATION_HOURS" --rate "$RATE" --points "$POINTS" \
  --ingress "localhost:${GRPC_INGRESS_PORT}" \
  --oxigraph "${OXIGRAPH_URL:-http://localhost:7878}" \
  --minio-endpoint "${MINIO_ENDPOINT_HOST:-localhost:9000}" \
  ${EXTRA_ARGS[@]+"${EXTRA_ARGS[@]}"} || rc=$?
echo "[s19] E10 soak done → $OUT (rc=$rc)"
exit $rc
