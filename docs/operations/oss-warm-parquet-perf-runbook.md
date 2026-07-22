# 性能テスト runbook — Parquet レイク経路（既定アーキ）

`Tools/e2e-performance` のハーネスを **Parquet レイク既定（#216）** に追従させた実行手順。従来の合格結果
（2026-05）は旧 TimescaleDB warm パスに対するもので、現行の既定アーキ（validated → 実
`ParquetLakeWriterWorker` → MinIO レイク → DuckDB 検証）は本 runbook で計測する。

> 環境注記: この測定はライブスタック（Docker）が必要。CI は手動起動のみ（クレジット節約）、リポジトリの
> サンドボックスでは Docker 不可。下記はローカル/専用環境で人手実行する手順。

## アーキの対応

| 役割 | 旧（timescale） | 新（parquet, 既定） |
|---|---|---|
| 取り込み変換 | `e2e_pipeline_bridge.py`（MQTT→NATS→**TS 直書き**） | `e2e_pipeline_bridge.py` `PARQUET_MODE=true`（MQTT→NATS validated のみ） |
| 永続化 | Python が TimescaleDB へ INSERT | 実 **ConnectorWorker `ParquetLakeWriterWorker`**（`WARM_STORE=parquet`）が MinIO `cold` に Parquet 書き込み |
| 検証 | `quality_checker.py --mode timescale`（psql） | `quality_checker.py --mode parquet`（DuckDB/S3 で MinIO レイクを集計） |

run の識別: ロードジェネレータが `device_id=perf-{type}-{run8}-{i}` / `point_id=perf-point-{run8}-...` に
`run_id[:8]` を埋め込むため、`test_run_id` カラム無しでもレイクを `LIKE '%{run8}%'` で絞り込める。

## 前提（スタック起動）

```bash
# OSS スタック（既定 = parquet）+ MQTT ブローカ（#25 で任意化）。connector-worker の flush を短く。
MQTT_HOST=building-os.mosquitto \
PARQUET_FLUSH_INTERVAL=1 \
  docker compose -f docker-compose.oss.yaml --profile mqtt up -d
```

- `WARM_STORE` 未指定 → parquet（既定）。`connector-worker` が `ParquetLakeWriterWorker` を起動。
- `PARQUET_FLUSH_INTERVAL=1`（分）で QUICK 実行でも 90 秒待機内に flush される（既定 5 分だと待ちが長い）。
- MinIO は host から `localhost:9000`（`quality_checker` の `--minio-endpoint` 既定）。

## 実行（S2 ベーススループット、QUICK）

```bash
QUICK=true bash Tools/e2e-performance/s2_baseline.sh
# = MODE=parquet（既定）。small scale 10 devices / 5 分 / expected 250 rows。
# 内部: bridge を PARQUET_MODE=true で起動 → load gen → 90s 待機 → quality_checker --mode parquet。
```

本番規模（medium / 1 時間）:

```bash
bash Tools/e2e-performance/s2_baseline.sh    # MODE=parquet, scale=medium, expected 75000
```

## 本番スケール実証 + 3 KPI（#297）

`s2_baseline.sh` はスループット/品質（loss/dup/invalid/rows）のみを測る。レビュー指摘の **3 KPI**
（① consumer pending が単調増加しない / ② ingest→queryable lag p95 ≤ flush+60s / ③ building・point
増加時の range p95）を**1 パスで採取**するのが `run_s2_production.sh` + `s2_scale_sweep.sh`。

> ⚠️ **専用ベンチ機で実行**（単一 WSL では NATS/MinIO/connector が同居し負荷源・蓄積が頭打ちになる）。
> flush は本番想定の **5 分**で起動する。

```bash
# 1) スタックを本番想定で起動（flush=5分）
MQTT_HOST=building-os.mosquitto PARQUET_FLUSH_INTERVAL=5 \
  docker compose -f docker-compose.oss.yaml --profile mqtt up -d

# 2) 1h 走行 + KPI①② 採取（kpi_sampler が NATS :8222 と Prometheus を周期ポーリング）
SCALE=medium PROFILE=baseline DURATION=3600 \
  bash Tools/e2e-performance/run_s2_production.sh
#   → results/<run>/production-report.md（quality + kpi-summary + lake-storage を集約）
#   → results/<run>/kpi-timeseries.jsonl（pending / freshness_lag の時系列）

# 3) KPI③（range p95 のスケール依存）を別途
SWEEP='1 3 10' bash Tools/e2e-performance/s2_scale_sweep.sh <run>-sweep
#   → results/<run>-sweep/scale-sweep.md
```

**スループット目標の注意（重要）**: `medium × baseline` は 250 dev × 5 pt / 60s ≈ **1,250 行/分**
（= 75,000 行/h）であり「**毎分1万件級**」には届かない。1 万件級を主張するなら重いシェイプを選ぶ:

| シェイプ | 概算スループット |
|---|---|
| `SCALE=large PROFILE=baseline` | ~5,000 行/分 |
| `SCALE=medium PROFILE=mixed` | ~12,500 行/分 |
| `SCALE=large PROFILE=mixed` | ~50,000 行/分 |

`PROFILE` を baseline 以外にする場合は、`EXPECTED` を実投入数に合わせて上書きすること（既定の自動値は
baseline 前提）。負荷源（`device_load_generator`）がベンチ機で目標レートを実際に出せるかを先に確認する。

**合否（gate 連携）**: `kpi-summary.json` は `{"axis":"E1-production", metrics:{pending_stable, freshness_lag_p95_ms, ...}}`
を出すので、`e2e/kpi-thresholds.yaml` に E1-production 軸を足せば `gate.py` で機械判定できる（実測後に追加）。

レガシー TimescaleDB を測る場合のみ:

```bash
MODE=timescale WARM_STORE=timescale \
  docker compose -f docker-compose.oss.yaml --profile timescale up -d
MODE=timescale QUICK=true bash Tools/e2e-performance/s2_baseline.sh
```

## レイク検証の単体実行

```bash
python Tools/e2e-performance/quality_checker.py \
  --run-id "<TEST_RUN_ID>" --mode parquet --expected 250 \
  --minio-endpoint localhost:9000 --bucket cold
# loss_rate ≤1% / duplicate_rate ≤0.1% / schema_invalid=0 / row_count>0 で PASS。
# 結果: Tools/e2e-performance/results/<run>/quality-check-result.json（"mode":"parquet" を含む）
```

## ストレージ効率 KPI（#219、Parquet 固有）

```bash
bash Tools/e2e-performance/measure_lake_storage.sh   # レイク総バイト / オブジェクト数 / building-hour 当たり（compaction KPI ≤2）
bash Tools/e2e-performance/measure_compression.sh    # 圧縮率
k6 run Tools/e2e-performance/k6/s9_warm_kpi.js       # warm クエリ KPI
```

## 既知の制限 / follow-up

- 本 PR で parquet 対応したのは `quality_checker` / bridge / **S2 ランナー**。S3/S4/S7 も同じ `MODE`
  env パターンで追従予定（quality_checker は既に mode 対応済みなので、各ランナーの「TS スキーマ手順
  スキップ + bridge `PARQUET_MODE` + flush 待機延長 + `--mode`」を S2 と同様に適用するだけ）。
- ロードジェネレータの payload は汎用形式で、実プロトコルコネクタ（BACnet/HVAC スキーマ）は通さず
  bridge が validated を直接生成する。よって本ハーネスは **ParquetLakeWriter 以降**（書き込み・compaction・
  読み取り）の性能を測る。プロトコルコネクタ込みの E2E は別途。
- 実行はライブスタック必須。Docker 不可環境では構文検証のみ（`py_compile` / `bash -n`）。
