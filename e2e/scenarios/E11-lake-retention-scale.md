# E11 — 大規模継続負荷下の Parquet 保存・compaction・保持期間

## 背景・位置づけ

[#263](https://github.com/gutp-bim/gutp-building-os-ri/issues/263)。[#261](https://github.com/gutp-bim/gutp-building-os-ri/issues/261)（多棟 2k→50k Point スケール、
完了・PR #270）が「取り込みが多棟スケールで壊れないか」を検証したのに対し、本軸は「その負荷が
**継続したときに Parquet の flush・compaction・保持期限（`LAKE_RETENTION_DAYS`）が壊れないか**」を
検証する。[E10（長時間ソーク）](E10-endurance-soak.md)の scenario 文書は #263 を明示的にスコープ外と
している（単一ホスト・単一建物のメモリ定常性が対象で、多棟・大規模の保持境界とは範囲が異なる、
follow-up と明記）。本軸がその follow-up。

E10 が採った**capped-run（短時間で完了する上限付き版）の前提**をそのまま踏襲する（E10 が #297 の
真の ≥72h ソークの手前で反復可能な短時間版を用意したのと同じ考え方）— 本軸も真の複数日保持ウィンドウ
ソークの代わりに、**10 分未満で完了する**短時間版から始める。

## capped-run が証明できること／できないこと（必読）

`LAKE_RETENTION_DAYS`（#217）が使う S3/MinIO ILM の `Expiration.Days` は**日単位の粒度**である
（`LakeRetentionLifecycle.Build` 参照）。10 分未満の run でこの日次境界を実際に跨ぐことはできない
（E10 が ≥72h を要求したのと同じ理由 — 短時間では定常性/境界のどちらも直接観測できない）。

capped-run が実際に証明できるのはこの 2 つ:

1. **ILM ルールが実際に MinIO に適用されているか**（`retention_ilm_rule_applied`）。既存
   `LakeRetentionLifecycleTest.cs` は `LakeRetentionLifecycle.Build()` が返す設定オブジェクトの形しか
   検証しておらず、実際に `PutLifecycleConfiguration` が MinIO に届いているかは未検証だった
   （#263 が閉じたいギャップそのもの）。
2. **保持境界の分類・集計ロジック**（`lake_retention_kpi.py`）が、run 中に実際に書かれた object の
   実測 age に対して正しく動くか（`retention_boundary_correct_ratio`）。

証明**できない**のは「保持期限を過ぎた object が実際に消える」こと — 10 分の run で作られる
object はどれも数分しか経っておらず、日単位の境界には遠く及ばない。この KPI は境界越えの実証では
なく、分類パイプラインへの回帰ガードとして読むこと。

## 「すでに settled な時間」への合成配置（compaction を待たずに起こす技法）

`ParquetLakeWriterWorker` はフレームの**イベント時刻**（受信時刻ではない）でパーティションを切る。
そのため、**すでに終わっている時間**（`settled_target_hour`、既定 3 時間前）のタイムスタンプでフレームを
送ると、`CompactionPlanner.IsHourSettled` は実行直後から真になる — 実際の壁時計が 1 時間進むのを待つ
必要がない。evaluation-report.md の E4/s14（agg_hour bimodal 解消、rollup-backed 化）が使ったのと
同じ技法。

## 測定経路

[plan.md §0](../plan.md) と同じ **gRPC GatewayIngress**。[#261](../scenarios) の
`s17_multibuilding_scale_sweep.build_topology` で多棟・多ゲートウェイのトポロジーを生成し、各棟へ
複数 wave（既定 3 回）を送って flush サイクルを複数回発生させ、compaction がそれを 1 object に
まとめるところまでを見る。

## 試験条件（既定、capped-run）

| 項目 | 値 |
|---|---|
| 点数 / 棟数 / ゲートウェイ数 | 300 / 3 / 6（`POINTS`/`BUILDINGS`/`GATEWAYS`） |
| wave 数 | 3（`WAVES`。各棟・各 settled hour に ≥2 part を作り compaction の `LAKE_COMPACTION_MIN_PARTS`(既定2) を満たす） |
| flush 間隔 | 1 分（`PARQUET_FLUSH_INTERVAL`。アプリ既定 5 分から短縮） |
| compaction 間隔 / settle grace | 1 分 / 0 分（`LAKE_COMPACTION_INTERVAL`/`LAKE_COMPACTION_SETTLE_MINUTES`。settle grace を 0 にできるのは、上記の「すでに settled な時間」技法でデータ自体が実時間の settle を必要としないため） |
| 保持日数 | 1 日（`RETENTION_DAYS`→`LAKE_RETENTION_DAYS`。ILM ルールの**適用確認**のみに使う — 上記の理由により実際の失効は 10 分の run では観測できない） |
| 合成 settled hour | 実行時刻の 3 時間前（`--target-hours-back`） |

## 手順

1. `bash Tools/e2e-performance/s20_retention_compaction.sh <out-dir>`（内部で以下を行う）:
   - OSS スタックを flush/compaction を短縮した env で起動（`docker-compose.oss.yaml`。
     `LAKE_RETENTION_DAYS` は既定 365 日 → capped-run では 1 日に上書き）。
   - `s20_retention_compaction.py` が多棟トポロジーを twin へ seed → 合成 settled hour への wave 送信
     → MinIO を polling して flush/compaction の成否・所要時間を記録 → ILM ルール適用確認
     （`check_ilm_rule`）→ 保持境界分類（`retention_observations` + `lake_retention_kpi.retention_boundary_report`）
     → `quality_checker.py`（parquet mode, building 単位）で棟ごとの loss/duplicate を算出。
2. `E11-retention.json`（gate 用の `{axis: E11_lake_retention_scale, metrics}`）を `<out-dir>` に残す。

## 重要指標

- **flush 成功率・latency**（`flush_success_rate` / `flush_latency_p50_ms` / `flush_latency_p95_ms`）:
  wave 送信後、MinIO 上の object 数増加をポーリングで検出するまでの時間。
- **compaction 成功率・latency**（`compaction_success_rate` / `compaction_latency_p50_ms` /
  `compaction_latency_p95_ms`）: 全 wave 送信後、各棟の settled hour が「compact object のみ」に
  収束するまでの時間。
- **保持境界の分類正解率**（`retention_boundary_correct_ratio`）: 上記「できないこと」参照 — 短時間 run
  では常に 1.0 になりうるが、分類ロジック自体の回帰は捕捉する。
- **compaction 後の object 数**（`objects_per_building_hour`）: E7 と同じ KPI を settled hour に対して。
- **データ整合**（`data_loss_ratio` / `duplicate_rate`）: `quality_checker.py` を棟ごとに実行し集計。
- **ILM ルール適用**（`retention_ilm_rule_applied`, report）: MinIO に実際に適用されているかの確認結果
  （`mc ilm rule list` の応答形式が読めなかった場合は metric 自体を出さず SKIP — 「未適用」と誤判定
  しない）。

## 合否（`e2e/kpi-thresholds.yaml`: `E11_lake_retention_scale`）

flush/compaction success rate ≥99% / flush latency p95 ≤ flush 間隔+マージン / compaction latency p95 ≤
compaction 間隔+マージン / `retention_boundary_correct_ratio` == 1.0（完全一致） /
`objects_per_building_hour` ≤ 2（compaction 後） / loss_rate ≤1% / duplicate_rate ≤0.5%
（E1 と同じ値を再利用）。ILM 適用確認・object 数の細目は report/informational。

## 既存資産・ギャップ

- **既存**: `s17_multibuilding_scale_sweep.py`/`s17_scale_stage.py`（多棟トポロジー生成、#261）、
  `quality_checker.py`（parquet mode, building 単位 loss/dup）、`normalize_storage.py`（E7 の
  object-per-partition 計測 — 本軸は同じ考え方を `lake_retention_kpi.objects_per_building_hour` として
  独立関数化）、`CompactionPlanner`/`CompactionWorker`（既存ユニットテストは pure planner のみ、
  実 MinIO は未検証）、`LakeRetentionLifecycle`/`LakeRetentionHostedService`（既存テストは構築した
  設定オブジェクトの形のみ検証、実適用は未検証 — 本軸が埋める）。E10 の capped-run 先例
  （`s19_endurance_soak.sh`の`MEM_LIMIT`変奏と同じ「短時間で完了する上限付き版を先に用意する」考え方）。
- **ギャップ**（本軸が新規に埋める）: 多棟スケール × 継続 flush/compaction の KPI 化
  （`lake_retention_kpi.py` + `s20_retention_compaction.py`）、ILM ルールの実 MinIO 適用確認
  （`check_ilm_rule`）、保持境界の分類ロジック（`classify_retention_boundary`/`retention_boundary_report`）。
- **残る既知の限界**: 真の複数日保持ウィンドウ（実際の失効の観測）は本 capped-run のスコープ外
  ——「capped-run が証明できること／できないこと」節を参照。`run-all.sh` の既定 `ONLY` には含めない
  （E10 と同じ理由 — 個別実行専用）。
