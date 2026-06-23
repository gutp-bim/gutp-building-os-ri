# Building OS 参照アーキテクチャ E2E 評価計画

本書は Building OS を **参照アーキテクチャ（reference architecture）として論文化**するための End-to-End
定量評価計画である。評価対象は nexus-gateway 単体ではなく、**取り込み・正規化・保存・検索・制御・運用** を含む
Building OS 全体（NATS JetStream / ConnectorWorker / GatewayIngress(gRPC) / ParquetLakeWriter /
MinIO Parquet Lake / TimescaleDB(opt-in) / API Server / Web Client / OxiGraph / Keycloak /
OpenTelemetry）。

> 既存の `Tools/e2e-performance/`（#219）の負荷生成・KPI スクリプトを土台に再利用する。本 `e2e/` は
> それらを **論文評価軸 E1–E8 に再編するオーケストレーション + 計画 + 結果スキーマ層** である。既存に無い
> 経路（gRPC GatewayIngress 正本・Point List 整合・control stale-replay）は「ギャップ」として明示する。

## 0. 評価対象アーキテクチャ（測定経路の正本）

```
gateway ──(gRPC GatewayIngress: gateway_id+point_id+value+ts)──► ConnectorWorker
        └ IPointMetadataCache で twin メタ付与 ─► NATS building-os.validated.telemetry
            ├─► ValidatedTelemetryHotStore ─► NATS KV telemetry-latest   (Hot: 最新1件/point)
            └─► ParquetLakeWriterWorker     ─► MinIO Parquet Lake          (Warm/Cold: 既定)
API Server /telemetries/query (Query Router: hot/warm/cold 自動選択) ─► Web Client
制御: API ─► NATS building-os.control.request ─► NatsPointControlWorker（in-proc）
      API ─► building-os.control.request.gw.{id} ─► GatewayBridge ─► BOWS（per-gateway, offline=503）
Digital Twin: OxiGraph(SPARQL)。Point List = (gateway_id, point_id) 契約。
```

測定の**正本経路は gRPC GatewayIngress**。MQTT(Mosquitto)/Hono(AMQP) は point list を共有しない
デバイス向けの副経路で、既存 `e2e_pipeline_bridge.py` はこの MQTT 経路のプロキシ。論文の ingest 評価は
gRPC 経路を第一とし、MQTT 経路は対照として併記する。

## 1. 評価軸（E1–E8）と論文での主張

| ID | 評価軸 | 論文での主張 | 優先度 |
|----|--------|-------------|--------|
| **E1** | Telemetry ingest 性能 | 実ビル規模の時系列を欠損・重複を抑え実用遅延で取り込める | 必須 |
| **E2** | Ingest E2E latency | gateway 生成→validated まで低遅延、Parquet 末尾鮮度は分単位を Hot で補完 | 必須 |
| **E3** | 最新値取得（Hot） | Hot を履歴 DB から分離し、保存方式に依存せず監視 UI 向け最新値を低遅延維持 | 必須 |
| **E4** | 履歴クエリ（Warm/Cold/Lake） | 最新=Hot・短期=Warm・長期=Parquet を同一 API で扱える | 必須 |
| **E5** | Point List / Twin 整合 | (gateway_id, point_id) 契約境界で現場プロトコル差を隠蔽し Twin で一元管理（本論文の独自性） | 必須 |
| **E6** | Control path 安全性 | Telemetry と Control に異なる信頼性ポリシーを与え、履歴耐障害性と制御安全性を両立 | 必須 |
| **E7** | 保存コスト・鮮度（Parquet vs TimescaleDB） | 秒単位鮮度を Hot に委ね、履歴を Parquet Lake に統合し監視性能と長期保存コストを分離 | 推奨 |
| **E8** | 障害復旧・可用性 | 部分障害時に Hot/Warm/Cold が graceful degradation し、復旧後に欠損なく回復 | 推奨 |
| E9 | 運用・可観測性 | OTel で ingest path を end-to-end に追跡でき、point_id/gateway_id/control_id でログ相関 | 補助 |

## 2. 負荷スケール・マトリクス

| スケール | points | interval | telemetry rate | 用途 |
|---------|--------|----------|----------------|------|
| small   | 100    | 60s | 1.7 pt/s | smoke / CI |
| medium  | 1,000  | 60s | 16.7 pt/s | 通常運用 |
| large   | 5,000  | 60s | 83.3 pt/s | 大規模運用 |
| stress  | 10,000 | 10s | 1,000 pt/s | 限界評価 |
| burst   | 10,000 | 1s | 10,000 pt/s | burst 吸収 |

通常運用評価は medium/large、限界評価は stress/burst。

## 3. KPI 閾値（pass/fail ゲート）

機械可読版は [`kpi-thresholds.yaml`](kpi-thresholds.yaml)。既存 `docs/oss-warm-parquet-kpi.md` の数値と整合。

| 軸 | 指標 | ゲート |
|----|------|--------|
| E1 | sustained throughput | 目標 rate を 99% 以上維持 |
| E1 | loss rate / duplicate rate / validation error rate | ≤1% / ≤0.5% / ≤1% |
| E2 | ingest E2E p95（gen→validated） | < 2s |
| E2 | parquet write freshness p95（event→queryable） | ≤ flush 間隔 + 60s |
| E3 | latest API p95 | < 500ms（stretch < 20ms） |
| E3 | latest freshness p95（event→hot 反映） | < 2s |
| E3 | stale latest ratio | < 1% |
| E4 | warm 24h/1pt p95 | < 2s |
| E4 | cold 7d/1pt p95 | < 5s |
| E4 | hour/day 集計 p95 | cold < 3s / cache hit < 100ms |
| E4 | multi-point scaling | point 数増に対し sublinear |
| E5 | point resolution success（有効 frame） | ≥ 99.9% |
| E5 | unknown point_id / 非所有 point の拒否 | 100%（受理ゼロ） |
| E5 | remapping correctness | 100% |
| E5 | twin lookup p95（point→building/device/unit） | < 50ms（cache） |
| E6 | command success rate（正常時） | ≥ 99% |
| E6 | command round-trip p95 | < 2s |
| E6 | **stale replay count** | **= 0** |
| E6 | duplicate write count | = 0 |
| E6 | not-writable 拒否 / offline→503 | 100% |
| E6 | typed failure 分類率 | 100%（timeout/no_connector/not_writable/device_error） |
| E7 | parquet bytes/row | ≤ TimescaleDB 非圧縮の 20%（≥80% 削減） |
| E7 | 1 building-hour あたり object 数（compaction 後） | ≤ 2 |
| E8 | data loss under outage（復旧後） | ≤ 1% |
| E8 | RTO 実測 / backlog drain time | 計測・記録（回帰監視） |

### 論文ヘッドライン 5 指標
1. **Ingest E2E p95 latency**（E2）
2. **Latest value API p95 latency**（E3）
3. **Historical query p95 latency**（E4）
4. **Point List resolution success rate**（E5）
5. **Control stale replay count = 0**（E6）

## 4. 評価軸 → 既存スクリプト対応とギャップ

| 軸 | 既存資産（`Tools/e2e-performance/`） | ギャップ（本 e2e で補完） |
|----|--------------------------------------|---------------------------|
| E1 | `s2_baseline.sh`, `s3_burst.sh`, `device_load_generator.py` | **gRPC GatewayIngress 負荷クライアント**（現状 MQTT 経路）|
| E2 | `s2_baseline.sh`（throughput） | gen→validated タイムスタンプ計測、`parquet_writer.freshness_lag`(Prom) 収集 |
| E3 | `k6/s5_api_read.js`, `k6/s9_warm_kpi.js`(`kpi_latest`) | freshness lag / stale-latest 比率の算出 |
| E4 | `k6/s9_warm_kpi.js`（warm/cold/agg/multipoint） | 30d レンジ、warm/cold 境界マージ遅延 |
| E5 | `s4_quality.sh`, `quality_checker.py`（validation error） | **unknown/非所有 point 拒否、remap、drift、twin lookup latency** |
| E6 | `s6_point_control.sh`, `k6/s6_point_control.js` | **stale replay / duplicate write / offline→503 / typed failure** シナリオ |
| E7 | `measure_lake_storage.sh`, `measure_compression.sh` | TimescaleDB 対照取得・月額コスト推定 |
| E8 | `s7_resilience.sh`, `s7_resilience_test.py` | RTO 実測・graceful degradation の体系化 |

各軸の詳細手順・入出力・合否判定は [`scenarios/`](scenarios/) を参照。

## 5. 実行方法（概要）

```bash
# 1) OSS スタック起動（Parquet 既定）
docker compose -f docker-compose.oss.yaml up -d

# 2) 全軸オーケストレーション（run id ごとに e2e/results/<id>/ へ出力）
bash e2e/runner/run-all.sh                 # 既定 medium
SCALE=large bash e2e/runner/run-all.sh     # スケール指定
ONLY=E3,E4 bash e2e/runner/run-all.sh      # 軸を限定

# 3) 個別軸
bash e2e/runner/run-axis.sh E1 --scale medium
```

結果は `e2e/results/<run-id>/` に JSON + サマリで集約し、[`results/report-template.md`](results/report-template.md)
に流し込んで論文評価表を作る。

## 6. 実験条件・再現性

- 計測ホスト・CPU/メモリ・コンテナ resource limit を `e2e/results/<run-id>/env.json` に記録。
- 各 run は warmup（破棄）→ 本計測の二段。p50/p95/p99 は本計測区間のみ。
- 乱数シード固定、point list は同一シードから生成（OxiGraphSeed）。
- WARM_STORE=parquet（既定）と timescale（opt-in）の双方で E4/E7 を取得し対照。
- CI ではなくローカル/専用ベンチ機で実行（CI はテスト系を手動起動のみに制限）。

## 7. 成果物（論文）

- 評価表 6 種（取り込み / 最新値 / 履歴クエリ / Point List 整合 / 制御安全性 / コスト保存効率）。
- ヘッドライン 5 指標の達成可否。
- アーキテクチャ図（本書 §0）＋ KPI と設計判断（Hot/Warm/Cold 分離、点 ID 契約）の対応。
