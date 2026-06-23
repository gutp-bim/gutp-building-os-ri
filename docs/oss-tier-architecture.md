# Hot / Warm / Cold 階層化テレメトリアーキテクチャ

Building OS OSS における時系列データの 3 層ストレージ設計と、API Query Router の仕様を定める。

**関連 Issue**: Epic #79（Hot/Warm/Cold 階層化アーキテクチャの確定とコスト最適化）

> **構成選択（WARM_STORE, Epic #211）**: `WARM_STORE` で Warm 層の構成を選べる。**既定は
> `parquet`（#216 で既定化）** — Warm を Parquet 直接書き込みにして Cold と単一レイク統合し、
> テレメトリ read/write は TimescaleDB 不要（point control は別途 `TIMESCALE_CONNECTION_STRING`
> が必要）。本ドキュメントは `WARM_STORE=timescale`（TimescaleDB を Warm とする旧既定）構成の正で、
> ロールバック先でもある。parquet 構成の詳細は
> [oss-warm-parquet-lake.md](oss-warm-parquet-lake.md)（PRD）と
> [ADR 0002](adr/0002-warm-store-parquet-lake.md) を参照。
>
> | モード | Warm 実装 | Cold 実装 | 集計 | 書き込み |
> |---|---|---|---|---|
> | `parquet`（既定） | `ParquetLakeTelemetryStore`（MinIO 単一レイク、warm=cold） | 同左 | `AggregatingParquetTelemetryStore`（aggregate-on-read） | `ParquetLakeWriterWorker`（validated→Parquet 直書き） |
> | `timescale` | `NpgsqlWarmTelemetryStore` | `MinioParquetColdTelemetryStore` | `NpgsqlAggregatedTelemetryStore`（continuous aggregate） | `ColdExportWorker` + Python `telemetry-consumer` |

---

## 1. アーキテクチャ概要

```
ConnectorWorker
  │ validated.telemetry (NATS subject)
  ▼
NatsKvPublisher ──────────────────────────────► NATS KV "telemetry-latest" [Hot]
  │
  ▼
ConsumerWorker
  │ INSERT
  ▼
TimescaleDB Hypertable "telemetry" [Warm]
  │
  ▼ (5〜15 分ごと: ColdExportWorker)
MinIO "cold/" バケット（Parquet/Zstd） [Cold]

                                API Server
                                  │
                    GET /telemetries/query
                                  │
                          OssTelemetryQueryRouter
                         /         |          \
                       Hot       Warm         Cold
                    NATS KV  TimescaleDB   MinIO Parquet
```

---

## 2. 各層の責務

### 2.1 Hot 層 — NATS KV `telemetry-latest`

| 項目 | 値 |
|------|-----|
| 実装クラス | `NatsKvLatestStore` (`Infrastructure/Oss/NatsKvLatestStore.cs`) |
| 書き込み | `NatsKvPublisher`（ConnectorWorker 内のデコレーター） |
| 保持内容 | 各 `point_id` の**最新 1 件**のみ（history=1） |
| 読み取り p95 目標 | < 20 ms |
| KV バケット名 | `telemetry-latest` |
| キー正規化 | `[^a-zA-Z0-9_.\-]` → `_` |
| 用途 | リアルタイムダッシュボードの最新値表示 |

**書き込みフロー:**

```
NatsKvPublisher.PublishAsync()
  ├─ NATS raw publish（既存パイプライン）
  └─ NatsKvLatestStore.PutAsync(pointId, data)
       └─ KV "telemetry-latest/{normalized_point_id}" に上書き
```

Hot 書き込みの失敗は例外を吸収してログ出力のみ（パイプラインを止めない）。

---

### 2.2 Warm 層 — TimescaleDB Hypertable `telemetry`

| 項目 | 値 |
|------|-----|
| 実装クラス | `NpgsqlWarmTelemetryStore` (`Infrastructure/Telemetry/NpgsqlWarmTelemetryStore.cs`) |
| 書き込み | `ConsumerWorker` → INSERT INTO telemetry |
| 保持期間 | 90 日（設計値）→ Cold 配線後は **14 日**に短縮推奨（HITL） |
| 圧縮開始 | `compress_after = 7 days` |
| チャンク間隔 | `chunk_time_interval = 1 day` |
| セグメントキー | `point_id` |
| 読み取り p95 目標 | < 200 ms（≤ 24h 範囲） |
| 集計ビュー | `telemetry_hourly`、`telemetry_daily`（continuous aggregate） |

pgBouncer (transaction pool, port 6432) 経由で接続する。EF Core マイグレーションのみ
session pool (port 6433) または direct 接続を使用する（advisory lock が必要なため）。

**集計クエリの優先度:**

```
granularity=Hour → telemetry_hourly ビュー（キャッシュ 5 分）
                   失敗時 → raw warm にデグラデーション
granularity=Day  → telemetry_daily ビュー（キャッシュ 5 分）
granularity=Raw  → telemetry テーブル直読み
```

---

### 2.3 Cold 層 — MinIO Parquet

| 項目 | 値 |
|------|-----|
| 実装クラス | `MinioParquetColdTelemetryStore`（読み）/ `NpgsqlMinioExportService`（書き） |
| 書き込みトリガー | `ColdExportWorker`（BackgroundService、`COLD_EXPORT_INTERVAL` 分ごと） |
| フラッシュ間隔 | 5〜15 分（`COLD_EXPORT_INTERVAL`、デフォルト 5 分） |
| 圧縮 | Parquet + Zstd |
| パス規則 | `cold/building_id={building}/year={Y}/month={MM}/day={DD}/hour={HH}/part-{ts}.parquet` |
| 読み取り p95 目標 | < 5 秒（過去 N 日〜N 月） |
| 保持 | 無期限（MinIO lifecycle で管理） |

**ColdExportWorker の動作:**

```
ループ（COLD_EXPORT_INTERVAL 分ごと）
  └─ IColdExportService.ExportChunkAsync(from, to)
       ├─ TimescaleDB から対象期間データを CSV 読み出し
       ├─ Parquet(Zstd) に変換
       ├─ MinIO "cold/" バケットに PUT
       └─ cold_export_log にエクスポート記録
```

現行実装: Warm の **drop_chunks は自動実行しない**（HITL: 保持期間ポリシーの確定後に追加）。

---

## 3. API Query Router — `OssTelemetryQueryRouter`

### 3.1 エンドポイント

| エンドポイント | 説明 |
|--------------|------|
| `GET /telemetries/query?pointId=&start=&end=&granularity=&latest=` | **正本**: 自動 tier 選択（hot/warm/cold/集計）。UI・ゲートウェイはこれを使う |
| `GET /telemetries/hot?pointId=` | 〔非推奨〕Hot 層 直接（後方互換） |
| `GET /telemetries/warm?pointId=&startTime=&endTime=` | 〔非推奨〕Warm 層 直接（後方互換） |
| `GET /telemetries/cold?pointId=&startTime=&endTime=` | 〔非推奨〕Cold 層 直接（後方互換） |

> **正本 API（#304）**: テレメトリ取得は `GET /telemetries/query` を正本とする（tier 自動選択）。per-tier の
> `/hot` `/warm` `/cold` `/cold-multi-point` は Swagger 上 deprecated 表示で、後方互換のため残置。新規実装は
> `/telemetries/query` を使うこと。

### 3.2 `/telemetries/query` のルーティングロジック

```
latest=true
  └─► Hot → 失敗時 Warm 最新値

granularity=Hour|Day
  └─► 集計ビュー（5 分キャッシュ）→ 失敗時 Raw Warm

granularity=Raw（デフォルト）
  │
  ├── end < (now - warmRetention)
  │   └─► Cold のみ
  │
  ├── start > (now - warmRetention)
  │   └─► Warm のみ
  │
  └── 境界をまたぐ場合
      ├─► Cold[start, boundary) + Warm[boundary, end]
      └─► 時系列順にマージして返却
```

`warmRetention` のデフォルトは 90 日。`AddOssTelemetryServices` の引数で変更可能。

### 3.3 レスポンスキャッシュ

集計クエリ（Hour / Day 粒度）は `IMemoryCache` で 5 分キャッシュする。  
キャッシュキー: `router:{pointId}:{granularity}:{start:yyyyMMddHH}:{end:yyyyMMddHH}`

---

## 4. 性能 KPI と実測値

| 指標 | 目標 | 実測（S2 QUICK 2026-05-21） |
|------|------|---------------------------|
| Hot read p95 | < 20 ms | — （S5 テストで 292 ms、Hot KV 未計測） |
| Warm read ≤24h p95 | < 200 ms | S5 結果: `latest_value_duration p95 = 292 ms`[^1] |
| Cold read p95 | < 5 s | — （S5 cold パス未計測） |
| Ingest E2E p95 | < 5 s | S2 損失率 0%、重複率 0%（10 devices/5 min） |
| Burst 吸収 | 損失率 < 1% | S3 burst 損失率 0%（10x 流量） |
| Schema 品質 | Invalid = 0 | S4 全フェーズ 0 件 |

[^1]: S5 の `latest_value_duration` は `/telemetries/hot` のエンドポイントレイテンシ全体（ネットワーク + 処理）を含む。NATS KV の raw レイテンシは別途計測が必要。

---

## 5. コスト設計方針

### 5.1 Warm 保持期間の短縮

| 設定 | 現行 | 推奨値（HITL 確認後） |
|------|------|-------------------|
| `compress_after` | 7 日 | 7 日（維持） |
| `drop_after`（retention） | 120 日 | **14 日**（Cold 配線確認後） |
| Warm 実効保持期間 | ~90 日 | **14 日** |

Warm 保持を 14 日にすることで TimescaleDB のストレージが約 **85〜90% 削減**（推定）。目標: 月額 50〜80 万円 → 20〜35 万円相当。

### 5.2 pgBouncer 接続管理

| Pool | Port | pool_mode | max_client_conn | default_pool_size |
|------|------|-----------|-----------------|-------------------|
| transaction | 6432 | transaction | 200 | 50 |
| session (migration only) | 6433 | session | 20 | 5 |

### 5.3 Parquet のコスト優位性

| メトリクス | TimescaleDB (Warm) | MinIO Parquet (Cold) |
|-----------|-------------------|---------------------|
| 圧縮率（見込み） | 8〜12x | 10〜15x（Parquet/Zstd） |
| GB あたりの月額（目安） | $0.10〜0.20 (SSD) | $0.02〜0.05 (Object Storage) |
| ランダムアクセス | ✅ 高速 | ❌ ファイル単位 |
| 集計クエリ | ✅ continuous aggregate | ⚠ DuckDB 直読み |

---

## 6. 環境変数リファレンス

| 変数 | 説明 | デフォルト |
|------|------|-----------|
| `POSTGRES_CONNECTION_STRING` | pgBouncer transaction pool 接続文字列 | — |
| `POSTGRES_MIGRATION_CONNECTION_STRING` | EF Core migration 用 session pool 接続文字列 | `POSTGRES_CONNECTION_STRING` にフォールバック |
| `MINIO_ENDPOINT` | MinIO エンドポイント | `building-os.minio:9000` |
| `MINIO_BUCKET_TELEMETRY` | Cold tier バケット名 | `telemetry` |
| `COLD_EXPORT_INTERVAL` | ColdExportWorker フラッシュ間隔 (分, 1〜15) | `5` |
| `WARM_RETENTION_DAYS` | Query Router の Warm 保持期間判定値 | `90` |

---

## 7. OSS リファレンス実装としての位置づけ

本アーキテクチャは [`docs/standard-mapping.md`](standard-mapping.md) の SBCO（`sbco:`）オントロジーに基づく
IoT ビルディングデータの **Hot/Warm/Cold 分離パターン**のリファレンス実装である。

| パターン | 実装 | 標準との対応 |
|---------|------|-----------|
| 最新値キャッシュ | NATS KV | `sbco:PointExt` の現在状態 |
| 時系列範囲クエリ | TimescaleDB Hypertable | `sbco:PointExt` の `time`-`value` 系列 |
| 長期アーカイブ | Parquet on MinIO | コールドデータ解析・規制対応 |
| 自動 tier 選択 | `OssTelemetryQueryRouter` | `GET /telemetries/query` |

最小プロファイル (`docker-compose.minimal.yaml`) では NATS + TimescaleDB + pgBouncer のみ起動。
Cold tier (MinIO / ColdExportWorker) と観測スタックはオプション。
Kubernetes 向けは `kubernetes/helm/building-os/values-minimal.yaml` を参照。

---

## 8. HITL 確認事項

- [ ] Warm 保持期間 14 日への短縮可否（Cold 書き込み完全性の確認後）
- [ ] `drop_after` の自動実行タイミング（Cold Parquet 検証完了後のみ）
- [ ] S5 Hot read p95 < 20 ms の実測（NATS KV 直アクセスの計測）
- [ ] Cold read p95 < 5 s の実測（MinIO Parquet ファイルサイズ依存）

---

## 9. 関連ドキュメント

- `docs/oss-timescaledb-schema.md` — Hypertable スキーマ・圧縮・continuous aggregate の詳細
- `docs/observability-baseline.md` — Prometheus/Loki/Tempo の cardinality ポリシーと retention
- `docs/standard-mapping.md` — SBCO（`sbco:`）ontology と外部標準語彙の対応表（`bos:` は ControlSchema 拡張）
- `PERFORMANCE_SUMMARY.md` — E2E テスト全 Scenario の実測値まとめ

---

*作成: 2026-05-22 / HITL レビュー: 未完了（§8 参照）*
