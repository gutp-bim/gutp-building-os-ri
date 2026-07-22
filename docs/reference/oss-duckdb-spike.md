# DuckDB クエリエンジン性能評価 Spike（#221）

## 背景

Parquet.Net によるレイク読み（v1: `ParquetLakeScan`）は、行グループ列単位
での手動デシリアライズのため述語プッシュダウンがなく、大きな time range や
多数 point_id での集計クエリが IO ボトルネックになりやすい。

DuckDB.NET は MinIO/S3 を直接 `read_parquet('s3://...')` で読み、
述語プッシュダウン・列プルーニング・vectorized 集計を透過的に利用できる。
v1 KPI が未達の場合（`cold 7d p95 ≥ 5s` 等）に投資対効果を事前評価する。

---

## ゴール

| # | ゴール |
|---|---|
| 1 | 同一データ/同一クエリ（24h raw / 7d raw / hourly 集計 / multi-point）で p50/p95 を比較 |
| 2 | コンテナイメージサイズ・ビルド複雑度・arm64 対応を評価 |
| 3 | 採否の推奨と判断根拠を results/ にレポート |

**非ゴール:** DuckDB を production に投入すること（採用は spike 結果次第）。

---

## 評価アーキテクチャ

### DuckDbLakeTelemetryStore

```csharp
// スパイク用の最小ベンチマークヘルパー。本番 store インターフェースは実装しない。
// 採用判断後に IWarmTelemetryStore / IColdTelemetryStore / IAggregatedTelemetryStore
// へ昇格させる想定。
public sealed class DuckDbLakeTelemetryStore : IDisposable
```

内部で `DuckDbQueryBuilder` が SQL を生成し、`DuckDB.NET.Data.DuckDBConnection`
の `CREATE SECRET ... TYPE S3` で MinIO 認証を設定してから `read_parquet` を実行。
現時点では `IDisposable` のみ実装した軽量ヘルパーで、`BenchmarkRunner` から直接呼ばれる。

### DuckDbQueryBuilder（pure、テスト可）

```sql
-- raw query
SELECT point_id, building, device_id, name, value, time, data, id
FROM read_parquet('{lake_glob}', hive_partitioning=true)
WHERE point_id = ? AND time BETWEEN ? AND ?

-- hourly agg
SELECT point_id, DATE_TRUNC('hour', time) AS hour_utc,
       AVG(value), MIN(value), MAX(value), COUNT(*) AS cnt
FROM read_parquet('{lake_glob}', hive_partitioning=true)
WHERE point_id = ? AND time BETWEEN ? AND ?
GROUP BY point_id, hour_utc ORDER BY hour_utc
```

パーティションプルーニングは MinIO のディレクトリ列挙依存のため、
glob は `{bucket}/building_id=*/year={Y}/month={MM}/day=*/hour=*/*.parquet`
の月単位精度で現実的に利用する。

### ベンチマーク runner

```
BuildingOS.DuckDbSpike/Program.cs
  --base-url  MinIO endpoint
  --minio-key / --minio-secret
  --point-ids comma-separated
  --runs      反復回数（既定 5）
  --from / --to  クエリ時間範囲

出力: p50/p95/p99 表（Parquet.Net vs DuckDB、4 クエリ種別）
```

---

## 評価軸

### 性能

| クエリ | Parquet.Net v1 目標 | DuckDB 期待値 |
|---|---|---|
| 24h raw | < 2s | < 500ms（述語プッシュ） |
| 7d raw | < 5s | < 2s（列プルーニング） |
| hourly 集計 | < 3s | < 500ms（vectorized） |
| multi-point 5件 | < 5s | < 1s |

### 運用コスト

| 軸 | Parquet.Net | DuckDB.NET |
|---|---|---|
| NuGet | `Parquet.Net` only | `DuckDB.NET.Data` + native lib |
| arm64 | ✅ pure managed | 要検証（DuckDB native binary） |
| イメージサイズ増加 | — | +30〜80MB 見込み（native lib） |
| ビルド複雑度 | low | medium（RID-specific package） |

---

## 新規ファイル

| ファイル | 役割 |
|---|---|
| `DotNet/BuildingOS.DuckDbSpike/BuildingOS.DuckDbSpike.csproj` | Exe、参照 Shared |
| `DotNet/BuildingOS.DuckDbSpike/DuckDbQueryBuilder.cs` | pure SQL 生成（glob・パラメータ） |
| `DotNet/BuildingOS.DuckDbSpike/DuckDbLakeTelemetryStore.cs` | IWarmTelemetryStore/IColdTelemetryStore/IAggregatedTelemetryStore |
| `DotNet/BuildingOS.DuckDbSpike/Program.cs` | ベンチマーク runner |
| `DotNet/BuildingOS.Shared.Test/.../DuckDbQueryBuilderTest.cs` | pure SQL 生成の unit test |
| `Tools/e2e-performance/results/duckdb-spike-report.md` | 結果テンプレート（実測は環境あり時に埋める） |

---

## 判断基準（採用可否）

| 条件 | 判定 |
|---|---|
| 7d raw p95 が Parquet.Net より 50% 以上改善 AND arm64 ✅ | ✅ 採用推奨 |
| 改善幅は大きいが arm64 ✗ | ⚠️ 条件付き（arm64 サポート待ち） |
| 改善幅 < 30% | ❌ 投資対効果低、Parquet.Net 継続 |
| 上記に加え aggregate-on-read p95 が既に < 3s（KPI クリア） | ❌ DuckDB 不要（rollup で十分） |

実測値は `Tools/e2e-performance/results/duckdb-spike-report.md` に記録し、
#222 rollup KPI と合わせて判断する。

---

## 受け入れ条件（issue #221 チェックリスト準拠）

- [ ] 同一データ/同一クエリ（24h raw / 7d raw / hourly 集計 / multi-point）で p50/p95 比較
- [ ] イメージサイズ・ビルド・arm64 対応の評価
- [ ] 採否の推奨と判断根拠を results/ にレポート
