# 事前集計 Parquet — Hourly Rollup（#222）

## 背景と動機

`AggregatingParquetTelemetryStore`（#215）は aggregate-on-read で
hour/day 集計を返す。長期クエリ（例: 30日 × 複数 point）では毎回 raw
Parquet をフルスキャンするため、集計レイテンシが row 数に比例する。

CompactionWorker（#217）が hour パーティションを確定するタイミングに
**rollup オブジェクト**（point ごとの avg/min/max/count を事前集計した
小さな Parquet）を併記すれば、集計クエリは raw スキャン不要になる。

---

## ゴール

| # | ゴール |
|---|---|
| 1 | CompactionWorker が hour 確定と同時に `agg_hourly/` rollup を書く |
| 2 | `RollupParquetTelemetryStore` が rollup からhour/day 集計を返す |
| 3 | rollup 欠損時は `AggregatingParquetTelemetryStore`（aggregate-on-read）へ per-hour フォールバック |
| 4 | 集計クエリ p95 が aggregate-on-read 比で改善し、cold < 3s KPI を安定達成 |

**非ゴール:** rollup のリアルタイム更新（settled hour 確定後のみ生成）、rollup の再集計 API。

---

## アーキテクチャ

### Rollup オブジェクト

- **バケット:** `cold`（テレメトリレイクと同一、MinIO 管理一元化）
- **キー:** `agg_hourly/building_id={b}/year={Y}/month={MM}/day={DD}/hour={HH}/agg-{yyyyMMddHH}.parquet`
- **スキーマ（9 カラム）:**

| カラム | 型 | 説明 |
|---|---|---|
| `point_id` | string | |
| `building` | string | |
| `device_id` | string | |
| `name` | string | |
| `avg` | double? | 非 null 値の平均 |
| `min_value` | double? | 非 null 値の最小 |
| `max_value` | double? | 非 null 値の最大 |
| `count` | int | 総行数（null 含む） |
| `hour_utc` | DateTime | bucket の開始 UTC |

- **圧縮:** Zstd（テレメトリ本体と同じ）
- **冪等性:** キーが決定的なため、再実行は上書き = 安全

### 書き込みフロー（CompactionWorker 拡張）

```
compaction 完了（compact-*.parquet 書き込み + 検証成功）
    ↓
RollupAggregator.Compute(deduped_rows) → RollupRow[]
    ↓
RollupSerializer.WriteAsync(rollup_rows, stream)
    ↓
IBlobStorage.PutAsync("cold", agg_hourly/...key, stream)
```

rollup 書き込みが失敗しても compaction 自体は完了とみなす（次サイクルで retry）。
メトリクス: `compaction.rollup_written` (Counter), `compaction.rollup_failures` (Counter)。

### 読み取りフロー（RollupParquetTelemetryStore）

```
QueryHourlyAsync(pointId, start, end)
    ↓
GetBuildingsAsync() → [b1, b2, ...]
    ↓
[start_hour .. end_hour) を 1h ずつ:
    for each b: key = RollupPartitionKey.AggKey(b, hour)
    → GetAsync("cold", key)
    If found: filter by pointId → RollupRow → ValidTelemetryData
    If not found: fallback AggregatingParquetTelemetryStore for that hour
    ↓
merge across hours, sort ascending
```

Day 集計: 同じく rollup を読み、day bucket で再集計（`TelemetryAggregator` の
`AggregationBucket.Day` 相当）。

### DI（ApiServer Startup.cs）

```
parquet mode:
  AggregatingParquetTelemetryStore(lake)      // fallback
  RollupParquetTelemetryStore(lake, fallback) // primary
  ↕
  OssTelemetryQueryRouter(agg: rollup_store)
```

---

## 新規ファイル

| ファイル | 役割 |
|---|---|
| `ParquetLake/RollupRow.cs` | record (9 フィールド) |
| `ParquetLake/RollupSerializer.cs` | pure Write/Read |
| `ParquetLake/RollupPartitionKey.cs` | pure キー生成 |
| `ParquetLake/RollupAggregator.cs` | pure: raw rows → RollupRow[] |
| `Telemetry/RollupParquetTelemetryStore.cs` | IAggregatedTelemetryStore |

## 変更ファイル

| ファイル | 変更 |
|---|---|
| `ParquetLake/CompactionWorker.cs` | CompactAsync 末尾に rollup 書き込みを追加 |
| `ApiServer/Startup.cs` | lakeAgg を RollupParquetTelemetryStore で置換 |
| `Infrastructure/BuildingOsMetrics.cs` | compaction.rollup_written/rollup_failures 追加 |

---

## KPI

| KPI | 基準 |
|---|---|
| hour 集計 p95 | < 3s（aggregate-on-read 比で改善） |
| rollup 冪等性 | 同一 hour を 2 回 compact しても結果不変 |
| rollup 欠損フォールバック | rollup なし hour は aggregate-on-read で補完（200 返却） |

---

## 受け入れ条件（issue #222 チェックリスト準拠）

- [ ] rollup 生成が compaction と同じ冪等性を持つ（キー決定的、上書き安全）
- [ ] hour/day 集計が rollup から返り、aggregate-on-read 比で p95 が改善
- [ ] rollup 欠損時は aggregate-on-read へデグレード（200 返却）
