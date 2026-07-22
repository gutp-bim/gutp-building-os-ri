# JetStream Tail Merge — Parquet モード鮮度ゼロ化（#220）

## 背景と動機

parquet モードでは、フラッシュ間隔（`PARQUET_FLUSH_INTERVAL`、既定 5 分）に
相当する末尾データがレイクに到達していない。最新値は Hot KV が担うが、
warm クエリ（`/telemetries/warm`、`/telemetries/query`）の時系列末尾は
フラッシュまでの空白が生じる。チャート末尾が空白になるユースケースに対し
この空白を埋める機構が必要。

---

## ゴール

| # | ゴール |
|---|---|
| 1 | warm クエリの `end` が直近（`now - PARQUET_TAIL_LOOKBACK_SEC`以内）のとき、未フラッシュ分を JetStream 経由で補完 |
| 2 | 鮮度 lag p95 < フラッシュ間隔（≤ 10s は将来目標、まず < flush + 60s を達成） |
| 3 | JetStream フェッチ失敗時は Parquet のみの結果へデグレード（可用性優先） |
| 4 | クエリレイテンシ p95 の増加 < 500ms（Parquet scan と並行フェッチ） |

**非ゴール:** cold クエリへの tail merge（末尾=現在から離れているため不要）、latest クエリ（Hot KV が一次）。

---

## アーキテクチャ

### 判定ロジック（TailMergePolicy）

```
ShouldMergeTail(end, now, lookbackSec):
    end > now - lookbackSec  ⇒  merge
    else                     ⇒  skip (cold / historical query)
```

`lookbackSec` の既定値は `PARQUET_FLUSH_INTERVAL * 3`（= 15分）を目安に
`PARQUET_TAIL_LOOKBACK_SEC`（既定 900）で上書き可能。

### フェッチ（NatsTailReader）

```
INatsJSContext.CreateOrderedConsumerAsync("BUILDING_OS_VALIDATED", {
    DeliverPolicy = ByStartTime,
    OptStartTime  = since,          // lake 最後の行の time + 1ns
})
```

- **Ephemeral ordered consumer**（永続化なし）
- フェッチ上限: `PARQUET_TAIL_MAX_MSGS`（既定 2000）
- タイムアウト: `PARQUET_TAIL_TIMEOUT_MS`（既定 3000ms）
- デコード: `ValidTelemetryEnvelope`（既存 JSON パーサ再利用）→ pointId でフィルタ

### マージ（TailMergedTelemetryStore）

```
QueryAsync(pointId, start, end, ct):
    if !TailMergePolicy.ShouldMergeTail(end, now, lookbackSec):
        return inner.QueryAsync(...)

    // 並行スタート
    var lakeTask = inner.QueryAsync(pointId, start, end, ct)
    var tailTask = TryReadTailAsync(pointId, start, end, ct)  // degrade on error

    await Task.WhenAll(lakeTask, tailTask)

    // マージ: lake 結果の id set を構築、tail から欠けている id のみ追加
    var lakeIds = lakeRows.Select(r => r.Id).ToHashSet()
    var merged  = lakeRows.Concat(tailRows.Where(r => !lakeIds.Contains(r.Id)))
    return DedupById(merged)  // ascending by time
```

### 設定 (EnvModule)

| 環境変数 | 既定 | 説明 |
|---|---|---|
| `PARQUET_TAIL_MERGE_ENABLED` | `true` | `false` で機能を完全無効化 |
| `PARQUET_TAIL_LOOKBACK_SEC` | `900` | この秒数以内の end 時刻なら tail merge |
| `PARQUET_TAIL_MAX_MSGS` | `2000` | 1 フェッチ最大メッセージ数 |
| `PARQUET_TAIL_TIMEOUT_MS` | `3000` | tail フェッチ タイムアウト(ms) |

### メトリクス

| 名前 | 種類 | 説明 |
|---|---|---|
| `building_os.parquet_lake.tail_merge_rows` | Counter | tail から補完した行数 |
| `building_os.parquet_lake.tail_merge_errors` | Counter | フェッチ失敗（デグレード）回数 |

---

## 新規ファイル

| ファイル | 役割 |
|---|---|
| `ParquetLake/TailMergePolicy.cs` | pure: ShouldMergeTail |
| `ParquetLake/IJetStreamTailReader.cs` | interface |
| `ParquetLake/NatsTailReader.cs` | NATS ordered consumer 実装 |
| `Telemetry/TailMergedTelemetryStore.cs` | IWarmTelemetryStore デコレーター |

## 変更ファイル

| ファイル | 変更 |
|---|---|
| `ApiServer/Modules/EnvModule.cs` | tail merge 設定 4 項目追加 |
| `ApiServer/Startup/Startup.cs` | parquet mode で TailMergedTelemetryStore をラップ |
| `Infrastructure/BuildingOsMetrics.cs` | tail_merge メトリクス 2 件追加（rows / errors） |

---

## KPI

| KPI | 基準 |
|---|---|
| 鮮度 lag p95 | ≤ flush 間隔 + 60s（`parquet_writer.freshness_lag` + tail_merge の合計） |
| クエリレイテンシ追加 p95 | < 500ms |
| tail 失敗時デグレード | 200 返却（lake のみ結果）、エラーログ |

---

## 受け入れ条件（issue #220 チェックリスト準拠）

- [ ] warm クエリ end が直近の場合に JetStream tail がマージされ、鮮度 lag p95 < 10s
- [ ] リプレイ失敗時は Parquet のみの結果へデグレード（可用性優先）
- [ ] クエリレイテンシ p95 の悪化が +500ms 以内
