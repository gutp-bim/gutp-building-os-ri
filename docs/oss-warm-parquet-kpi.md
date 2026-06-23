# Warm Parquet Lake KPI 計測・評価 Runbook（#219）

`WARM_STORE` 切替（timescale → parquet）の効果を計測し、PRD
[oss-warm-parquet-lake.md](oss-warm-parquet-lake.md) §7 の KPI に対する合否を**評価レポート**として
残すための手順。Epic #211 の評価ゲート。

> 実測には稼働中の OSS スタック（`docker compose -f docker-compose.oss.yaml`）+ シード済みデータ +
> [k6](https://k6.io) が必要。本書は**手順とレポート様式**を提供する。実測値は run して
> `Tools/e2e-performance/results/` のレポートに記入する（数値を捏造しないこと）。

## 計測ツール

| ツール | 用途 |
|---|---|
| `Tools/e2e-performance/k6/s9_warm_kpi.js` | latest / warm 24h / cold 7d / hour・day 集計 / multi-point の p95（両モード同一負荷） |
| `Tools/e2e-performance/measure_lake_storage.sh` | lake の総バイト・オブジェクト数・building-hour あたりオブジェクト数 |
| `Tools/e2e-performance/measure_compression.sh` | TimescaleDB 圧縮比（比較基準） |
| メトリクス（OTLP → Prometheus） | `parquet_writer.{rows,flushes,flush_duration,failures,freshness_lag,dropped}` / `compaction.*` / `building_os.telemetry.queries{tier,result}` |

## 両モードの実行手順

```bash
# 1) timescale モードで起動 → 負荷
WARM_STORE=timescale docker compose -f docker-compose.oss.yaml --profile timescale up -d
#   （シード・ウォームアップ後）
TS_LOG="Tools/e2e-performance/results/$(date -u +%Y%m%dT%H%M%SZ)-s9-timescale.log"
BASE_URL=http://localhost:5000 MODE=timescale VUS=10 DURATION=10m \
  k6 run Tools/e2e-performance/k6/s9_warm_kpi.js | tee "$TS_LOG"
bash Tools/e2e-performance/measure_compression.sh | tee -a "$TS_LOG"

# 2) parquet モードに切替（既定）→ 同一負荷
WARM_STORE=parquet docker compose -f docker-compose.oss.yaml up -d
PQ_LOG="Tools/e2e-performance/results/$(date -u +%Y%m%dT%H%M%SZ)-s9-parquet.log"
BASE_URL=http://localhost:5000 MODE=parquet VUS=10 DURATION=10m \
  k6 run Tools/e2e-performance/k6/s9_warm_kpi.js | tee "$PQ_LOG"
bash Tools/e2e-performance/measure_lake_storage.sh | tee -a "$PQ_LOG"

# 3) DB 依存 KPI: parquet モードで TimescaleDB(telemetry) を止めても read/write が成立することを確認
#    docker compose -f docker-compose.oss.yaml stop building-os.postgres  # ※ point control は別途要
```

## KPI 計測マッピング（PRD §7 と 1:1）

| 分類 | KPI | 合否基準 | 計測方法 |
|---|---|---|---|
| コスト | telemetry 経路の DB 依存 | parquet で TimescaleDB 停止でも telemetry read/write 成立（point control は対象外） | parquet 起動 → postgres stop → s9 latest/warm/cold が 200、ingest 継続（`parquet_writer.rows` 増加） |
| コスト | ストレージ効率 | parquet bytes/row ≤ timescale 非圧縮比の 20%（≥80% 削減） | `measure_lake_storage.sh` の総バイト ÷ 行数 vs `measure_compression.sh` の before |
| コスト | ファイル数 | compaction 後 1 building-hour ≤ 2 オブジェクト | `measure_lake_storage.sh` の「Max objects in any single building-hour」 |
| 性能 | latest p95 | < 500ms（劣化なし） | s9 `kpi_latest_duration` p95（両モード比較で非劣化） |
| 性能 | warm 24h/1point p95 | < 2s | s9 `kpi_warm_24h_duration` p95 |
| 性能 | cold 7d/1point p95 | < 5s | s9 `kpi_cold_7d_duration` p95 |
| 性能 | hour/day 集計 p95 | cold < 3s / cache hit < 100ms | s9 `kpi_agg_hour_duration` / `kpi_agg_day_duration` p95（連続 run で cache hit を観測） |
| 鮮度 | ingest→queryable lag p95 | ≤ flush 間隔 + 60s | `parquet_writer.freshness_lag` histogram p95（Prometheus） |
| スループット | writer 追従性 | 持続負荷で consumer pending 単調増加なし | NATS consumer pending（`nats consumer info` / Prometheus）を負荷中に観測 |
| 信頼性 | flush 失敗率 | < 0.1%・損失ゼロ | `parquet_writer.failures` / `parquet_writer.flushes`、障害注入（MinIO 一時停止）後の再配信で行数不変 |

## レポート様式

実測ごとに `Tools/e2e-performance/results/<UTCタイムスタンプ>-warm-parquet-kpi-report.md` を
[テンプレート](../Tools/e2e-performance/results/warm-parquet-kpi-report.template.md)から作成し、
各 KPI の実測値・合否（✅/❌）・出典ログを記入する。

**未達 KPI の扱い**: ❌ の項目は改善 issue を起票し、PRD の後続候補
（#220 JetStream tail マージ / #221 DuckDB / #222 事前集計 parquet）または新規 issue に紐づけて
本レポートからリンクする。
