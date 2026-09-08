# E12 — 混在負荷ベンチマーク（compaction 中の ingress/control tail latency）

## 背景・位置づけ

[#401](https://github.com/gutp-bim/gutp-building-os-ri/issues/401)（[#399](https://github.com/gutp-bim/gutp-building-os-ri/issues/399) Capability-based Worker Runtime PRD の子②）。
`WORKER_ROLE`（#400、[docker-compose.roles.yaml](../../docker-compose.roles.yaml) の role 分割オーバーレイ）は
すでにマージ済みで、`docs/reference/worker-role-split-measurement.md`（#400/#401 の一次計測）が
「分割しても壊れない」ことは確認したが、「分割の**効果**」（compaction 中の tail latency 安定性、
ingest だけの水平スケール）は測っていないと明記している。本軸がその効果を測る。

その一次計測の残課題（[worker-role-split-measurement.md §5](../../docs/reference/worker-role-split-measurement.md)）が、
そのまま本軸のスコープになる:

- compaction 実行中 / 非実行中で分けた ingest p95 / p99 の比較（本軸の核心測定）
- telemetry 負荷あり / なしでの control RTT の比較
- CPU・メモリ・GC pause・JetStream consumer lag（writer lag）の系列記録
- `WORKER_ROLE=all`（単一プロセス）と role 分割構成を**同一シナリオ**で走らせ、その差を示す

## #399 の 6 条件（分割の要否判定）

[#399](https://github.com/gutp-bim/gutp-building-os-ri/issues/399) は、ConnectorWorker のデプロイを
role 分割すべきかどうかの判断材料として次の 6 条件を挙げている。本軸（単一 run のベンチマーク）が
**実測できるのは 1〜4 のみ**であり、`mixed_load_kpi.split_decision` はそれをそのまま反映する
判定関数として実装されている（5・6 は `not_applicable_from_this_run` として明示し、黙って省かない）:

| # | 条件 | 本軸で測定可能か |
|---|---|---|
| 1 | ingest CPU が概ね 60〜70% を継続して超える | ○（`ingest_cpu_high_ratio`） |
| 2 | compaction 実行中に ingress p95/p99 が明確に劣化する（**本軸の核心測定**） | ○（`ingest_e2e_compaction_p95/p99_degradation_ratio`） |
| 3 | Parquet writer lag（JetStream consumer lag）が増加トレンドを示す | ○（`consumer_pending_slope_per_sec`） |
| 4 | 並行 telemetry 負荷で control RTT が有意に引きずられる | ○（`control_rtt_p95_degradation_ratio`） |
| 5 | ゲートウェイ数の増加で ingest ロールだけのスケールが必要になる | ×（複数期間・フリート全体の観測が要る。単一 run では測定不能） |
| 6 | デプロイが Kubernetes SLO 定義の段階に入る | ×（組織的・運用上の判断であり、ベンチマーク run では判定できない） |

`split_decision(...)` は 1〜4 のうち**実際にデータが揃った条件だけ**を `met: true/false` で判定し
（データが無ければ `insufficient_data` — 「未測定」を「条件を満たさない」と混同しない）、5・6 は常に
`not_applicable_from_this_run` として返す。`split_recommended` は 1〜4 のいずれかが `met: true` なら
`True`、1〜4 が全て測定できて全て `met: false` なら `False`、1〜4 が 1 つも測定できなければ `None`
（「分割不要と分かった」と「何も分からなかった」を区別する）。

## 測定経路

[plan.md §0](../plan.md) と同じ **gRPC GatewayIngress**（sustained ingest）+
**Eclipse Hono 相当の in-process control 経路**（`ENABLE_SIM_CONTROL=true` の
`NatsPointControlWorker` — [k6/s6_point_control.js](../../Tools/e2e-performance/k6/s6_point_control.js) が
API 経由で叩く既存の control 経路そのもの、変更なし）。

## 試験条件（既定、capped-run）

| 項目 | 値 |
|---|---|
| role 構成 | `all`（既定）または `split`（`docker-compose.roles.yaml` オーバーレイ） |
| 総実行時間（phase 2: 並行負荷） | 300 秒（`DURATION_S`） |
| ingest レート | 20 frames/s（`INGEST_RATE`）、50 points（`INGEST_POINTS`） |
| control 負荷 | k6 VUs=3（`CONTROL_VUS`） |
| control baseline 単独区間（phase 1） | 45 秒（`CONTROL_BASELINE_DURATION_S`、並行 ingest 無し） |
| flush / compaction 間隔・settle grace | 1 分 / 1 分 / 0 分（s20 と同じ理由 — 合成 settled hour 技法により実時間の settle を要しない） |
| 強制 compaction の対象時間 | 実行時刻の 3 時間前（`TARGET_HOURS_BACK`、s20 の `settled_target_hour` を再利用） |
| リソースサンプリング間隔 | 5 秒（`--sample-interval`） |

## 手順

1. `bash Tools/e2e-performance/s21_mixed_load_benchmark.sh <out-dir>`
   （`ROLE_MODE=split bash ... ` で role 分割構成、既定は `all`）。内部で `s21_mixed_load_benchmark.py` が:
   - `--role-mode` に応じてスタックを起動（`all`: 単一 `building-os.connector-worker`。`split`:
     `docker-compose.roles.yaml` を重ねて `-lake`/`-control` を追加起動）。
   - ingest 用の points と、強制 compaction 専用の points（**別集合** — sustained ingest の
     パーティションと衝突させない）、および control 用の書き込み可能 point を twin へ seed。
   - **phase 1**: `k6/s6_point_control.js` を単独で `CONTROL_BASELINE_DURATION_S` 秒走らせ、
     control RTT の無負荷ベースラインを採取（`--out json=` で生サンプルをダンプし、k6 の
     `control_submission_duration` Trend を自前で再集計する — `--summary-export` は事前集計済み
     percentile しか出さないため、compaction 窓での再バケット同様の生データが必要）。
   - **phase 2**: gRPC ストリームで sustained ingest を張りながら（`s11_ingest_latency.py` と同様に
     `building-os.validated.telemetry` を購読して ingest E2E latency をサンプリングするが、
     終了時の集計ではなく **経過秒つきで逐次記録**する）、同時に k6 control 負荷を並行実行し、
     run の 1/4 経過時点で s20 の `settled_target_hour` 技法により強制 compaction wave を送信、
     `compaction_converged`（MinIO の part→compact 遷移）をポーリングして収束した経過秒を記録。
     並行して 5 秒毎に CPU%・RSS・JetStream consumer pending をサンプリング。
   - `mixed_load_kpi.ingress_compaction_comparison` で ingest latency サンプルを収束時刻の前後で
     バケット化し p95/p99 劣化比を算出、`control_rtt_load_comparison` で baseline/concurrent の
     control RTT を比較、`split_decision` で #399 の判定を行う。
2. `<out-dir>/kpi-summary.json`（gate 用の `{axis: E12_mixed_load_benchmark, metrics, split_decision}`）
   と `<out-dir>/report.md`（人間可読レポート — 実行条件・主要指標・判定表）を出力。生データは
   `ingest-latency-samples.jsonl` / `resource-timeseries.jsonl` に残る。

## 重要指標

- **compaction 中/非中の ingest E2E p95・p99 とその劣化比**（`ingest_e2e_during/not_during_compaction_p95/p99_ms`,
  `ingest_e2e_compaction_p95/p99_degradation_ratio`）— 本軸の核心測定（#399 条件2）。
- **control RTT の無負荷/並行負荷比較**（`control_rtt_baseline_*` / `control_rtt_concurrent_*` /
  `control_rtt_p95/p99_degradation_ratio`）— #399 条件4。
- **ingest ロール CPU 高稼働率**（`ingest_cpu_high_ratio`）— #399 条件1。
- **JetStream writer lag の後半回帰スロープ**（`consumer_pending_slope_per_sec`）— #399 条件3。
  E10 の `pending_stable` と同じ `kpi_sampler._slope` を再利用（E10 は「発散しないか」の不変条件、
  本軸は「増加トレンドがあるか」を見る判定材料という位置づけの違いに注意）。
- **#399 判定結果**（`split_decision` オブジェクト全体。`metrics` には `split_decision_split_recommended`
  等の要約値のみ平坦化して載せる — gate.py は `metrics` しか読まないため）。

## 合否（`e2e/kpi-thresholds.yaml`: `E12_mixed_load_benchmark`）

`ingest_e2e_compaction_p95/p99_degradation_ratio` ≤ 1.5・`control_rtt_p95_degradation_ratio` ≤ 1.5・
再起動 = 0・OOM = 0。両バケット/両側にサンプルが無ければ該当 KPI は SKIP（gate.py の既定挙動）。
CPU 高稼働率・writer lag スロープ・compaction 収束有無・#399 判定の要約値は report（情報値）。

## 既知の限界・免責事項

- **クライアント/サーバ同一ホストの交絡（disclosed）**: `docs/reference/worker-role-split-measurement.md`
  が自身の計測について明記した限界と同じ — この harness はロードジェネレータ（本スクリプトと k6）と
  被測定スタックを同一ホストで動かす。絶対遅延値は両者の CPU 奪い合いに影響される。本軸の比較
  （compaction 中/非中の**比**、負荷あり/なしの**比**）は同一 run 内の相対比較なので、この交絡の影響は
  絶対値ほど大きくないが、role 分割構成では分割された各プロセスがそれぞれ CPU を要求するため、
  交絡そのものは「分割の効果」の一部を覆い隠しうる。複数コアに余裕のある専用ホスト（クライアント/
  サーバ分離）での追試が望ましい — worker-role-split-measurement.md §5 が既に指摘した通り。
- **capped-run（数分）**: E10/E11 と同じ前提。#399 条件1・3（CPU 継続超過・writer lag の**長期**トレンド）
  は数分の run では判断材料が限定的であり、`ingest_cpu_high_ratio` や
  `consumer_pending_slope_per_sec` は「この run の間はこうだった」以上の主張をしない。
- **#399 条件 5・6 は測定不能（意図的）**: `split_decision` は常に `not_applicable_from_this_run` として
  返す — ゲートウェイ数増加のスケール要否も Kubernetes SLO 定義の段階も、単発ベンチマークでは判定
  できない組織的判断であり、それを判定できるかのように見せないための明示。
- **強制 compaction は合成 settled hour 技法に依存**（s20 と同じ限界 — 実際の壁時計 1 時間・保持期限
  境界の到達は capped-run のスコープ外。本軸が compaction について見るのは「実行中/実行後の latency
  への影響」のみで、compaction 自体の正しさは E11 が担当）。
- **control 経路は in-process ハンドラ（Hono/Kandt 相当のシミュレータ）** — GatewayBridge 経由の
  BacnetSim/BOWS 経路（`building-os.control.request.gw.{gatewayId}`）は対象外。role 分割時の
  `building-os.connector-worker-control` が担うのはこの in-process 経路。
- **単一ホスト・単一建物**。E11（多棟スケール）とは範囲が異なる。
- `run-all.sh` の既定 `ONLY` には含めない（E10/E11 と同じ理由 — 数分かかるため）。CI では実行しない
  （本リポジトリの CI は手動起動のみ）。

## 既存資産・ギャップ

- **既存（変更なし・そのまま再利用）**: `s11_ingest_latency.py`（ingest E2E latency 計測の作法 —
  NATS 購読で gen→validated を計る手法。本軸は終了時集計ではなく経過秒つき逐次記録に変える点が
  異なる）、`s15_ingest_throughput.py`（sustained gRPC 負荷の作法）、
  `s6_point_control.sh`/`k6/s6_point_control.js`（control 負荷生成、無改変）、
  `s20_retention_compaction.py`（`settled_target_hour`/`spread_timestamps`/`partition_prefix`/
  `list_lake_keys`/`compaction_converged`/`stream_wave` — 強制 compaction 技法一式）、
  `s19_endurance_soak.py`（`docker_stats`/`docker_restart_state` — RSS・再起動監視）、
  `kpi_sampler.py`（`sample_pending`/`_slope` — JetStream consumer lag とその回帰スロープ）、
  `docker-compose.roles.yaml`（role 分割オーバーレイ、無改変で再利用）。
- **ギャップ（本軸が新規に埋める）**: compaction 窓（実行中/非実行中）で分けた latency バケット化と
  劣化比の算出、control RTT の無負荷/並行負荷比較、role 構成を CLI フラグで切り替えて同一シナリオを
  走らせるオーケストレーション、#399 の 6 条件に対する構造化判定（`mixed_load_kpi.split_decision`）。
- **残る既知の限界**: 上記「既知の限界・免責事項」参照。
