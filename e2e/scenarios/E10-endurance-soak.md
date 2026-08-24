# E10 — 長時間ソーク（endurance soak）

## 背景・位置づけ
[#297](https://github.com/gutp-bim/gutp-building-os-ri/issues/297) で THX 点リスト（1,865 点/300 秒周期、
MQTT 経路）による 24 時間 E2E 試験を実施した。データ経路は 537,121/537,121 件を完全一致で受理・永続化し、
再起動・OOM はゼロだったが、Connector Worker RSS が 281.6→497.5 MiB、Building OS NATS RSS が 44.96→145.0 MiB
増加した。毎時 Parquet compaction に対応する鋸歯状変動は説明がつくが、それを差し引いた定常 baseline が
上昇していないかは複数日試験でないと判断できない（#297 の acceptance criteria は **最低 72 時間**）。

本軸 E10 は、その 72h 版に至る前の **e2e/ 評価軸への統合 + 反復可能な短時間版**（既定 4–6h、この
worktree での初回実行時点）。`run-all.sh` の既定 `ONLY` には含めない — 個別実行のみ。

## #297 以降の改修との関係（2026-08-24 時点の見直し）
- テレメトリ API の `value` が union 型化され `valueText`/`valueBool` が撤廃された（#358/#359/#364/#366/#369）。
  **Parquet の保存列（4 列: value_num/value_text/value_bool/state 相当）は変更されていない**（#359 の PR 説明
  および #348 の計測記録に明記）— ソーク試験が直接依存する `quality_checker.py` の Parquet 列読み取りは
  影響を受けない。API 応答のみを見る場合は union 型のデコードが必要だが、本軸は Parquet 直読 + NATS
  monitoring + gRPC ingress のみを使い、API JSON は経路に含まれないため実質無関係。
- 性能試験のシーダー（#338/#300）と品質チェッカー（#342/#343/#345）が現行 twin バリデーション・判別値に
  追随済み — 本ソークが再利用する `s10_pointlist_integrity.insert_point` / `quality_checker.py` は現行に整合。
- twin 側の変更（levels 直下の equipment、`rec:Room`→`sbco:Room` materialize）はソークの対象外（point
  resolution の可否は起動直後の `wait_visible` で確認するのみ）。
- ADR-0005 の閾値設定手段が明確化された（#367）。E10 のメモリ閾値はまだこの ADR の管理下にない
  informational KPI のため対象外だが、baseline が確定した将来の #297 完了時に検討する。

## 測定経路
[plan.md §0](../plan.md) の正本経路と同じ **gRPC GatewayIngress**（#297 は MQTT 経路だった点が異なる —
plan.md が ingest 評価の第一経路と定めているのが gRPC のため、e2e/ 統合版はこちらを使う。MQTT 経路の長時間
特性は別途 nexus-gateway 側で評価する）。

## 試験条件（既定）
| 項目 | 値 |
|---|---|
| 点数 | 1,865（#297 の THX スケールを踏襲） |
| 周期換算レート | 約 6/s（1,865 点 ÷ 300 秒 ≈ 6.2/s） |
| 継続時間 | 4–6h（この worktree での実施値。#297 の確定版は ≥72h） |
| flush/compaction 間隔 | アプリ既定値のまま（意図的に速めない — 実運用の鋸歯パターンを観測するため） |
| リソースサンプリング間隔 | 60s |
| gRPC ストリーム再接続間隔 | 300s 毎（#297 は単一 24h ストリーム。本版は connector-worker が
  試験中に再起動しても継続測定できるよう小分けにする） |

## 手順
1. `docker compose -f docker-compose.oss.yaml up -d`（GRPC_INGRESS_PORT は compose 既定で 5051 有効）。
2. `DURATION_HOURS=4 RATE=6.2167 POINTS=1865 bash Tools/e2e-performance/s19_endurance_soak.sh <out-dir>`
   （内部で `s19_endurance_soak.py` が下記を並行実行）:
   - gRPC ストリームで持続負荷を送信し続ける（chunk 毎に sent/accepted を記録）。
   - 60 秒毎に `docker stats` で対象コンテナ（connector-worker/nats/oxigraph/minio。API Server は
     gRPC ingress 経路に含まれないため本軸では起動・計測しない）の RSS、
     `docker inspect` で再起動回数・OOM有無、各サービスの health エンドポイント、NATS の
     `BUILDING_OS_VALIDATED` consumer pending（`kpi_sampler.py` のロジックを再利用）をサンプリングする。
3. 終了後、`quality_checker.py`（parquet mode）で送信数と lake 永続化数を突き合わせ、loss/duplicate を
   算出する。
4. `resource-timeseries.jsonl` / `ingest-timeseries.jsonl` の生データと `E10-soak.json`（gate 用の
   `{axis, metrics}`）を `<out-dir>` に残す。

## 重要指標
- **定常性（#297 由来）**: 開始1時間平均 vs 終了1時間平均の RSS、後半のみの回帰スロープ（MiB/h）。
  安全域はまだ未確定のため informational（`report`）。
- **不変条件（gate 対象）**: コンテナ再起動 0、OOM 0、送受信データ整合（loss ≤1% / dup ≤0.5%）、
  health probe 成功率 ≥99.9%、NATS consumer pending の後半スロープが発散しない（`pending_stable`）。

## 合否（`e2e/kpi-thresholds.yaml`: `E10_endurance_soak`）
再起動 = 0 / OOM = 0 / data_loss_ratio ≤ 1% / duplicate_rate ≤ 0.5% / health_probe_success_rate ≥ 99.9% /
pending_stable = 1。RSS 増加量そのものは report（安全域は #297 の ≥72h 確定試験で定める）。

## 既知の限界・#297 との差分
- 4–6h では #297 が懸念した「24h retention 到達後の定常化」やそれ以降の長期トレンドは観測できない
  （NATS `BUILDING_OS_VALIDATED` の MaxAge は 24h — `DotNet/BuildingOS.Shared/Infrastructure/Telemetry/
  ParquetLake/ParquetLakeWriterWorker.cs`）。より長い run で再実行することが前提。
- managed heap / GC heap / LOH / thread 数の分離（#297 の調査項目）はコンテナ RSS のみでは得られない
  — 必要なら Connector Worker 側に `dotnet-counters` 等を後付けする別軸とする。
- ローカル単一ホスト・単一建物。#263（多棟・大規模 Parquet 保持境界）とは範囲が異なる — 統合は follow-up。
- `run-all.sh` の既定 gate には含めない（数時間かかるため）。CI では実行しない（このリポジトリの CI は
  手動起動のみ）。
