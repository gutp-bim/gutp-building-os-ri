# TimescaleDB スキーマ設計と Cold 階層化方針

Issue #19 成果物

---

## 1. 背景

現行スタックは以下の三階層で時系列データを保持する。

| 階層 | ストレージ | 読み取り遅延 | 保持期間 |
|------|-----------|------------|---------|
| Hot  | CosmosDB (最新 1 件)  | ~50 ms | 常時最新 |
| Warm | CosmosDB (変更フィード) | ~100 ms | 1 週間 |
| Cold | Blob Storage → MS Fabric Lakehouse | 1–5 s | 無期限 |

OSS 移行後の目標構成:

| 階層 | ストレージ | 読み取り遅延 | 保持期間 |
|------|-----------|------------|---------|
| Hot  | NATS JetStream last-value KV | ~10 ms | 最新 1 件 |
| Warm | TimescaleDB Hypertable | ~100 ms | **14 日**（Cold 配線確認後） |
| Cold | MinIO (Parquet) | 1–5 s (DuckDB 直読み) | 無期限 |

詳細アーキテクチャは [`oss-tier-architecture.md`](oss-tier-architecture.md) を参照（PR#93 でマージ予定）。

---

## 2. Hypertable スキーマ

```sql
CREATE TABLE telemetry (
    time        TIMESTAMPTZ     NOT NULL,   -- UTC タイムスタンプ (パーティションキー)
    point_id    TEXT            NOT NULL,   -- センサー識別子 (例: "bldg01/floor3/room301/temp")
    building    TEXT,                       -- ビル識別子
    device_id   TEXT,                       -- デバイス識別子
    name        TEXT,                       -- センサー名称
    value       DOUBLE PRECISION,           -- 数値測定値
    data        JSONB,                      -- 機器固有 raw ペイロード
    id          TEXT                        -- CosmosDB 移行時の元ドキュメント ID（将来削除候補）
);
```

### CosmosDB フィールドマッピング

| CosmosDB フィールド | TimescaleDB カラム | 型変換 |
|-------------------|------------------|--------|
| `datetime` (string) | `time` (TIMESTAMPTZ) | `::TIMESTAMPTZ` キャスト |
| `point_id` | `point_id` | そのまま |
| `building` | `building` | そのまま |
| `device_id` | `device_id` | そのまま |
| `name` | `name` | そのまま |
| `value` | `value` | そのまま |
| `data` (JSON string) | `data` (JSONB) | `::JSONB` キャスト |
| `id` | `id` | そのまま (トレーサビリティ用) |

---

## 3. パーティション設計

- **`chunk_time_interval = 1 day`**（決定済み）
  - S2/S3 ベーススループットテスト（2026-05-21）でチャンク境界をまたぐクエリに問題なし
  - 1 日あたりのデータ量: センサー数 × 測定頻度 × 1 日
  - 例: 1,000 センサー × 1 件/分 = 1,440,000 行/日
  - チャンクサイズ目安: 圧縮前 ~300 MB → 圧縮後 ~30 MB（8〜12x 見込み）
- セカンダリ次元: `point_id` (compress_segmentby で活用)

---

## 4. 圧縮ポリシー

```sql
ALTER TABLE telemetry SET (
    timescaledb.compress,
    timescaledb.compress_segmentby = 'point_id',
    timescaledb.compress_orderby   = 'time DESC'
);
SELECT add_compression_policy('telemetry', compress_after => INTERVAL '7 days');
```

### チューニング決定（S2/S3/S4 実測ベース）

| パラメータ | 決定値 | 根拠 |
|-----------|--------|------|
| `chunk_time_interval` | **1 day** | S2/S3 テストで日次境界に問題なし。チャンク数 = 14（14d Warm）と管理可能 |
| `compress_after` | **7 days** | 直近 7 日のクエリが最も頻繁（ダッシュボード範囲）。7 日超はバッチ解析が主体 |
| `drop_after` | **14 日**（HITL: Cold 配線確認後に適用） | ColdExportWorker が 5〜15 分ごとに Parquet 書き出し済み。長期保持は Cold に委譲 |

**`data JSONB` 列の扱い:**
- Warm 層: `data` 列を保持（プロトコル固有デバッグ用途）
- Cold 層（Parquet）: `data` 列も Parquet に含まれる（`VARCHAR`/JSON 文字列に変換）
- 圧縮率への影響: `data` が NULL の行はセグメントキー圧縮で高率に圧縮される
- Cold 移行後に `data` 列を Warm で NULL 化することで圧縮率 10〜30% 改善の見込み（HITL）

**`id TEXT` 列の必要性:**
- CosmosDB 移行トレーサビリティ用の元ドキュメント ID
- OSS スタックでは新規書き込み時に `id` は不要
- **判定: 将来の削除候補**。移行完了確認後にマイグレーションで DROP（HITL 確認）

---

## 5. Cold 階層化方針

### 5.1 エクスポートジョブ仕様（実装済み）

`ColdExportWorker`（BackgroundService、`COLD_EXPORT_INTERVAL` 分ごと）:

1. `IColdExportService.ExportChunkAsync(from, to)` を定期実行
2. `IExportDataReader.ReadAsync(from, to)` で TimescaleDB からデータ読み出し
3. `NpgsqlMinioExportService` が Parquet(Zstd) に変換して MinIO に PUT
4. `cold_export_log` にエクスポート記録

> 当初設計（月次 K8s CronJob）から変更: **BackgroundService による継続フラッシュ**に変更済み。

### 5.2 MinIO Parquet パス規則（実装値）

```
cold/
  building_id={building}/
    year={YYYY}/
      month={MM}/
        day={DD}/
          hour={HH}/
            part-{timestamp}.parquet
```

### 5.3 Continuous Aggregate — `telemetry_hourly`

```sql
-- refresh schedule（V001 実装値）
SELECT add_continuous_aggregate_policy('telemetry_hourly',
    start_offset => INTERVAL '3 days',
    end_offset   => INTERVAL '1 hour',
    schedule_interval => INTERVAL '1 hour');
```

V001 の実装値（`start_offset=3 days / schedule_interval=1 hour`）が正。S2 テスト実績では 1 時間間隔でも遅延なし（small scale）。
本番スケール（100 devices × 100 msg/s）での影響は中規模テスト（medium scale）で再計測が必要（HITL）。

---

## 6. 保持ポリシー

| フェーズ | 操作 | タイミング | 現行実装値 |
|---------|------|----------|-----------|
| Warm 圧縮 | `add_compression_policy` | 7 日経過後（自動） | V001 で有効 |
| Cold エクスポート | `ColdExportWorker` | 5〜15 分ごと（継続） | 実装済み |
| Warm 削除（安全網） | `add_retention_policy` | **120 日**（V001 実装値） | V001 で有効 |
| Warm 削除（目標） | `add_retention_policy` 変更 | **14 日**（HITL 確認後） | 未適用 |

> **注意（現行状態）**: V001 マイグレーションにより `drop_after=120 days` の retention policy がすでに有効。
> 14 日への短縮は Cold Parquet の書き込み完全性確認後に以下の手順で切り替える:
>
> ```sql
> -- 既存の安全網 policy を削除してから再登録
> SELECT remove_retention_policy('telemetry');
> SELECT add_retention_policy('telemetry', drop_after => INTERVAL '14 days', if_not_exists => TRUE);
> ```

---

## 7. マイグレーション実行手順

```bash
# OSS スタック起動後
docker compose -f docker-compose.oss.yaml up -d building-os.postgres

# マイグレーション適用
docker exec -i building-os.postgres \
  psql -U buildingos -d buildingos \
  < DotNet/BuildingOS.Shared/Migrations/Timescale/V001__telemetry_hypertable.sql

docker exec -i building-os.postgres \
  psql -U buildingos -d buildingos \
  < DotNet/BuildingOS.Shared/Migrations/Timescale/V002__point_control_audit.sql
```

---

## 8. 圧縮率実測スクリプト

以下のスクリプト（`Tools/e2e-performance/measure_compression.sh`）を実行することで、TimescaleDB の実測圧縮率・チャンクサイズを取得できる。
事前に `compress_after` で圧縮済みチャンクが存在する必要がある（7 日以上経過後）。

E2E パフォーマンス実測結果のサマリーは [`Tools/e2e-performance/PERFORMANCE_SUMMARY.md`](../../Tools/e2e-performance/PERFORMANCE_SUMMARY.md) を参照。

```bash
#!/usr/bin/env bash
# Tools/e2e-performance/measure_compression.sh
docker exec building-os.postgres psql -U buildingos -d buildingos <<'SQL'
-- チャンク別圧縮状況
SELECT
    chunk_name,
    before_compression_total_bytes / 1024.0 / 1024.0 AS before_mb,
    after_compression_total_bytes  / 1024.0 / 1024.0 AS after_mb,
    ROUND(
        before_compression_total_bytes::numeric
        / NULLIF(after_compression_total_bytes, 0), 2
    ) AS compression_ratio
FROM chunk_compression_stats('telemetry')
ORDER BY chunk_name;

-- 全体サマリー
SELECT
    pg_size_pretty(before_compression_total_bytes) AS before_total,
    pg_size_pretty(after_compression_total_bytes)  AS after_total,
    ROUND(
        before_compression_total_bytes::numeric
        / NULLIF(after_compression_total_bytes, 0), 2
    ) AS total_compression_ratio
FROM (
    SELECT
        SUM(before_compression_total_bytes) AS before_compression_total_bytes,
        SUM(after_compression_total_bytes)  AS after_compression_total_bytes
    FROM chunk_compression_stats('telemetry')
) s;
SQL
```

---

## 9. HITL レビューチェックリスト

| 項目 | 状態 | 根拠 |
|------|------|------|
| `chunk_time_interval = 1 day` が実際のデータ量に対して適切か | **決定済み（維持）** | S2/S3 QUICK テストで問題なし |
| `compress_after = 7 days` がクエリパターンに対して適切か | **決定済み（維持）** | 直近 7 日が主要クエリ範囲 |
| `drop_after = 14 days` の適用（Warm 削除有効化） | **HITL 必須** | Cold 配線完全性の運用確認後 |
| `data JSONB` 列の Warm 側 NULL 化（圧縮率改善） | **HITL 必須** | Cold 側に raw データが残るか確認 |
| `id TEXT` 列の DROP マイグレーション | **HITL 必須** | CosmosDB 移行完了確認 |
| `telemetry_hourly` MV refresh コスト（本番スケール） | **要実測** | medium scale S2 テストで再計測 |
| 圧縮率の実測値確認 | **要実測** | §8 スクリプト実行後に更新 |

---

## 10. 圧縮率実測結果（S2 QUICK、2026-05-21）

| 条件 | 値 |
|------|-----|
| テストスケール | small（10 devices × 5 pts/msg） |
| 計測期間 | 300 秒 |
| 総行数 | 250 行 |
| 計測時点のチャンク状態 | 未圧縮（compress_after = 7 日のため） |

> S2 QUICK はデータ量が小さく（250 行）、TimescaleDB の圧縮チャンクは生成されない。
> 圧縮率の実測は medium scale（250 devices × 1 時間以上）の実施後、§8 スクリプトで取得する。

*作成: 2026-05-22 / HITL レビュー: §9 参照（一部決定済み）*
