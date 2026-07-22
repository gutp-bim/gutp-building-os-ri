# TimescaleDB → Parquet レイク 移行 Runbook（#218）

既存の `WARM_STORE=timescale` 環境を、コスト最小の `WARM_STORE=parquet`（統合 Parquet レイク）へ
**無停止・ロールバック可能**に移行する手順。新規 OSS デプロイには不要（最初から parquet 既定）。

> 前提: Epic #211 の #212–#217 がデプロイ済み（lake reader / ParquetLakeWriterWorker / 集計 / compaction /
> retention）。バックフィル CLI は `DotNet/BuildingOS.LakeBackfill`。

## 全体方針

1. **併走**: timescale モードのまま ParquetLakeWriterWorker を併走起動し、**切替時点以降**の telemetry を
   レイクにも書き始める（二重書き）。
2. **バックフィル**: 切替時点より**前**の既存 TimescaleDB データを CLI で一括移行。
3. **検証**: 行数照合 + サンプルクエリの timescale/parquet 一致確認。
4. **切替**: `WARM_STORE=parquet` に変更（API・worker 再起動）。
5. **停止**: 安定確認後に TimescaleDB（telemetry）を停止/縮退。
6. **ロールバック**: いつでも `WARM_STORE=timescale` に戻すだけ（**レイクのデータは消さない**）。

冪等性: バックフィルも ParquetLakeWriterWorker も**決定的命名**で同一オブジェクトを上書きするため、
併走期間が backfill 範囲と重複しても重複行は read 時 id dedup + compaction で吸収される。

---

## 手順

### 0. 事前確認
- `MINIO_ENDPOINT` / 資格情報が API・worker・CLI から到達可能。
- レイク用 `cold` バケットが存在。
- 切替時点 `T_cutover`（UTC）を決める（例: 次のメンテ枠の開始時刻）。

### 1. ParquetLakeWriterWorker を併走起動（二重書き）
切替前から `T_cutover` 以降のデータをレイクに載せるため、ParquetLakeWriterWorker だけを先行起動する。
ColdExportWorker（timescale）と ParquetLakeWriterWorker は通常**排他**なので、併走には
**`WARM_STORE=parquet` を明示した別インスタンスの ConnectorWorker** を 1 つ追加で起動する
（既存の timescale 用 worker はそのまま）。

```bash
# 追加で起動する併走 worker（parquet writer のみを担当）
WARM_STORE=parquet \
MINIO_ENDPOINT=http://building-os.minio:9000 \
NATS_URL=nats://building-os.nats:4222 \
dotnet run --project DotNet/BuildingOS.ConnectorWorker
```
> durable consumer 名は固定（`parquetlakewriter`）なので、追加インスタンスを増やしても
> JetStream 上は単一の論理コンシューマとして負荷分散される。

### 2. 既存データをバックフィル
`T_cutover` より前の範囲を CLI で移行する。期間を分割して進めてよい（再実行安全）。

```bash
# ドライラン（書き込みなしで対象行数を確認）
dotnet run --project DotNet/BuildingOS.LakeBackfill -- \
  --from 2026-01-01T00:00:00Z --to 2026-06-01T00:00:00Z --dry-run

# 本実行（TIMESCALE_CONNECTION_STRING / MINIO_* は env からでも可）
dotnet run --project DotNet/BuildingOS.LakeBackfill -- \
  --from 2026-01-01T00:00:00Z --to 2026-06-01T00:00:00Z \
  --timescale "Host=...;Database=buildingos;Username=...;Password=..." \
  --minio http://building-os.minio:9000
# building 単位に絞る場合: --building <buildingId>
```
出力末尾の **Reconciliation**（rows read / written / objects）で移行行数を確認。

### 3. 検証
- **行数照合**: CLI の `rows read` が TimescaleDB の該当範囲の件数と一致（dedup 差分は NOTE 行に出る）。
- **クエリ一致**: 同一 point・期間で `WARM_STORE=timescale` の API 応答と、`WARM_STORE=parquet` の
  API 応答（別ポートで一時起動した API インスタンス）を数件サンプル比較。
- **最新値**: Hot KV（NATS）は両モード共通なので latest は不変。

### 4. 切替
API サーバと ConnectorWorker の `WARM_STORE` を `parquet` に設定して再起動。
- compose: `WARM_STORE=parquet docker compose -f docker-compose.oss.yaml up -d`
  （`telemetry-consumer` は `timescale` プロファイル限定なので自動的に停止対象）。
- k8s/Helm: `warmStore: parquet`（既定）を適用。
切替後は ParquetLakeWriterWorker が正規の書き込み経路、ColdExportWorker は無効化される。

### 5. TimescaleDB 停止 / 縮退
parquet モードで read/write が安定（数日）したら TimescaleDB の telemetry 負荷を停止できる。
- **注意**: point control（`IPointControlRepository`）は引き続き `TIMESCALE_CONNECTION_STRING` を
  必要とする。telemetry 用とは別管理（telemetry だけを止める / hypertable を縮退）。
- compaction（#217）と retention（`LAKE_RETENTION_DAYS`）はレイク側で継続。

### 6. ロールバック
問題が出たら **`WARM_STORE=timescale` に戻して再起動**するだけ（compose は `--profile timescale` を
付け、`telemetry-consumer` を復帰）。レイクのオブジェクトは削除しないので、再切替も即可能。
TimescaleDB を停止済み（手順5）の場合は telemetry 書き込み先を復帰してから戻すこと。

---

## チェックリスト

- [ ] 併走 ParquetLakeWriterWorker 起動、`parquet_writer.*` メトリクスが増加
- [ ] backfill ドライラン → 本実行、Reconciliation で行数一致
- [ ] サンプルクエリが timescale/parquet で一致
- [ ] `WARM_STORE=parquet` 切替、API 応答・points 画面が正常
- [ ] 数日安定後に TimescaleDB telemetry を停止（point control は維持）
- [ ] ロールバック手順（env 戻し）を関係者に周知
