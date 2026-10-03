# Parquet レイクの保持運用（RustFS / #492）

`LAKE_RETENTION_DAYS` の既定は **`0`（無期限）** です。Parquet レイク（MinIO 互換ストア上のバケット
`cold`）は自動では失効せず、**ディスク使用量は取り込みに比例して増え続けます**。この Runbook は、
自動失効の代わりに容量を運用で管理する手順です。

## なぜ ILM を使わないのか

OSS スタックの `building-os.minio` は RustFS 1.0.0 です。RustFS 1.0.0 は、バケットに S3 lifecycle
（ILM）ルールが 1 つでもあると、`x-amz-expiration` ヘッダを AWS SDK が解釈できない形式（RFC 3339）で返し、
`PutObject` / `GetObject` / `HeadObject` がクライアント側で例外になります（`ListObjectsV2` /
`DeleteObject` は影響なし）。`LakeRetentionHostedService` は `LAKE_RETENTION_DAYS > 0` のとき起動時に
ILM ルールを適用するため、**ConnectorWorker 起動直後からレイクの読み書きが恒久的に失敗します**。

- 回避策: `LAKE_RETENTION_DAYS=0`（compose の既定）。ILM は一切発行されません。取り込み・compaction・
  クエリへの影響はありません。
- 恒久対応: upstream 修正（rustfs/rustfs#8057）を含む**安定版**のリリース待ち。追跡は
  [#492](https://github.com/gutp-bim/gutp-building-os-ri/issues/492) /
  [#489](https://github.com/gutp-bim/gutp-building-os-ri/issues/489)。

> **`LAKE_RETENTION_DAYS` を 1 以上にしないでください**（RustFS を使う限り）。一度 ILM ルールが
> 付いたバケットは、ルールを削除するまで読み書きできません（下記「誤って設定した場合」）。
> 本番 IaC の MinIO（`opentofu/modules/minio`）は RustFS ではないため対象外です。

## 容量の監視

```bash
mc alias set bos-oss http://localhost:9000 \
  "${MINIO_ROOT_USER:-buildingos}" "${MINIO_ROOT_PASSWORD:-buildingos123}"   # 初回のみ
mc du bos-oss/cold                      # 合計サイズ
mc ls --recursive --summarize bos-oss/cold | tail -3   # オブジェクト数と合計
```

ホスト側のボリューム使用率（`minio_data`）も併せて監視し、**空きが 20% を切る前**に下記を実施します。
目安は [oss-warm-parquet-kpi.md](oss-warm-parquet-kpi.md) の bytes/row から見積もります。

## 古いデータの手動削除

保持日数 N（例: 365）より古いオブジェクトを削除します。**削除は不可逆**です。先に
[バックアップ](oss-backup-restore-runbook.md)（`mc mirror`）を取り、必ず `--dry-run` で対象を確認します。

```bash
# 1. 対象の確認（削除しない）
mc rm --recursive --force --older-than 365d --dry-run bos-oss/cold

# 2. 実行
mc rm --recursive --force --older-than 365d bos-oss/cold
```

- `--older-than` はオブジェクトの更新時刻（≒ 書き込み・compaction 時刻）で判定します。パーティション
  （`building_id=…/year=…/month=…/day=…/hour=…/`）の時刻と最大で compaction の遅延ぶん（settle 30 分 +
  周期）ずれますが、保持の用途では無視できます。
- 削除は compaction と競合しません（settle 済みの hour だけが対象。直近の hour は残ります）。
- 実行頻度は月 1 回程度で十分です。cron 化する場合も `--dry-run` の結果をログに残してください。
- 削除後も API は削除済み期間の `GET /api/v1/telemetries/query` を空で返します（エラーにはなりません）。

> 手順は mc の仕様に基づきます。初回は RustFS 上で `--dry-run` の出力を確認してから実行してください
> （mc 依存ツールが RustFS で別の不具合を起こす例: #491）。`mc` が使えない場合は、AWS CLI の
> `aws s3 rm --recursive --endpoint-url …` とパーティションのプレフィックス指定で同等のことができます。

## 誤って `LAKE_RETENTION_DAYS` を設定した場合

1. ConnectorWorker を止め、`LAKE_RETENTION_DAYS=0`（または未設定）に戻します。
2. バケットの lifecycle ルールを削除します（`LakeRetentionHostedService` の rule id は固定）:
   ```bash
   mc ilm rule ls bos-oss/cold
   mc ilm rule rm --all --force bos-oss/cold
   ```
3. ConnectorWorker を再起動し、`mc ls bos-oss/cold` と `GET /api/v1/telemetries/query` で読み書きが
   戻ったことを確認します。

## RustFS の安定版が出たら

修正入りの安定版が pull できることを確認 → ステージングで `LAKE_RETENTION_DAYS=365` を設定し、
Put / Get / Head と compaction が通ることを確認 → compose の既定を戻し、この Runbook を更新します
（#492 の TODO）。
