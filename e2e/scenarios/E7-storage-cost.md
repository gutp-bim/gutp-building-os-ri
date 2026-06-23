# E7 — 保存コスト・鮮度（Parquet Lake vs TimescaleDB）

## 目的
秒単位の末尾鮮度を Hot 層に委ね、履歴保存を Parquet Lake（Object Storage 中心・低コスト）に統合する
ことで、監視性能と長期保存コストを分離できることを示す（Futaba 以後の低コスト化の主張）。

## 計測指標
- storage size: 同一データを TimescaleDB / Parquet+Zstd で保存した容量。
- compression ratio: raw telemetry に対する圧縮率。
- monthly estimated cost: DB 構成（常時稼働 + SSD）vs Object Storage 構成の月額推定。
- write freshness lag: event time → Parquet 反映（E2 と共有）。
- flush duration / small file count（1 building-hour あたり object 数）。
- compaction reduction ratio（compaction 前後のファイル数・容量）。
- dedup effectiveness（id dedup による重複非表示率）。

## 手順
1. 同一データセットを `WARM_STORE=parquet` と `timescale` で投入。
2. `measure_lake_storage.sh`（総バイト・object 数・building-hour 最大 object 数）。
3. `measure_compression.sh`（圧縮前後）。
4. TimescaleDB 側容量を `pg_total_relation_size` 等で取得し対照。
5. クラウド料金表（Object Storage 単価 / DB インスタンス + SSD）で月額推定。

## 合否（kpi-thresholds.yaml: E7_storage_cost）
parquet bytes/row ≤ TimescaleDB 非圧縮の 20%（≥80% 削減）/ compaction 後 ≤ 2 object/building-hour /
月額コストは DB 構成対比で併記（report）。

## 既存資産・ギャップ
- 既存: `measure_lake_storage.sh`, `measure_compression.sh`, `docs/oss-warm-parquet-kpi.md`。
- **ギャップ**: TimescaleDB 対照容量の自動取得、月額コスト推定スクリプト。
