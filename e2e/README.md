# e2e — Building OS 参照アーキテクチャ E2E 評価

Building OS 全体（取り込み・正規化・保存・検索・制御・運用）の論文向け定量評価の **計画・評価項目・
オーケストレーション・結果スキーマ** をまとめたフォルダ。

- **[evaluation-report.md](evaluation-report.md)** — 最新の実測レポート（gate 結果 + ヘッドライン指標）。
- **[plan.md](plan.md)** — 評価計画の正本（評価軸 E1–E8、負荷スケール、KPI 閾値、論文評価表、ギャップ）。
- **[kpi-thresholds.yaml](kpi-thresholds.yaml)** — pass/fail ゲート（機械可読）。
- **[scenarios/](scenarios/)** — 軸ごとの詳細手順・入出力・合否判定（E1–E8）。
- **[runner/](runner/)** — 実行オーケストレーション（既存 `Tools/e2e-performance/` を再利用）。
  `run-all.sh`（全軸）/ `run-axis.sh`（単一軸）/ `gate.py`（結果 JSON × KPI 閾値の自動突合）。
- **[results/](results/)** — run ごとの出力（gitignore）。各 run に `kpi-report.md` + `gate.json` が生成される。

## 位置づけ（既存資産との関係）

実装済みの負荷生成・KPI スクリプトは `Tools/e2e-performance/`（#219）にある。本 `e2e/` はそれを
**論文評価軸 E1–E8 に再編する上位層**であり、スクリプトを重複実装しない。既存に無い経路
（gRPC GatewayIngress 正本・Point List 整合・control stale-replay）は各 scenario に「ギャップ」
として記し、段階的に実装していく。

## クイックスタート

```bash
# OSS スタック（Parquet 既定）
docker compose -f docker-compose.oss.yaml up -d

# 全軸（medium）
bash e2e/runner/run-all.sh

# スケール/軸を限定
SCALE=large ONLY=E3,E4 bash e2e/runner/run-all.sh
```

結果は `e2e/results/<run-id>/` に出力。`run-all.sh` は最後に **KPI ゲート**（`gate.py`）を実行し、各軸の
結果 JSON（`{axis, metrics}`）を `kpi-thresholds.yaml` と突合して `kpi-report.md`（ヘッドライン5指標
+ 全 KPI 表）と `gate.json` を生成する。比較可能な KPI が 1 つでも FAIL なら `run-all.sh` は非ゼロ終了。
データの無い軸・`report`/`sublinear`・動的閾値（null）は SKIP/INFO（FAIL 扱いにしない）。

```bash
# 単独でゲートだけ再実行（既存 run に対して）
python e2e/runner/gate.py e2e/results/<run-id>
```

## 前提

`Tools/e2e-performance/README.md` と同じ（uv / Python 3.11+ / k6 / Node 22+ / Playwright /
docker compose）。CI ではなくローカルまたは専用ベンチ機で実行する（CI のテスト系ワークフローは
クレジット節約のため手動起動のみ）。

## ステータス（実装の進捗）

最新の実測は [evaluation-report.md](evaluation-report.md) を参照（gate **PASS**）。

| 軸 | 状態 | 備考 |
|----|------|------|
| E1 ingest throughput | 🟢 計測済 | `s15`(gRPC 持続負荷) → quality_checker。throughput 1.0 / loss 0 / dup 0 / invalid 0 ✅（6000 frames）。s2/s3 は MQTT 補助 |
| E2 ingest latency | 🟢 計測済 | `s11`。ingest E2E p95 2.7ms / freshness ✅。gate 点灯 |
| E3 latest value | 🟢 計測済 | `s9` latest p95 51ms + `s13` freshness p95 13ms / stale 0.0 ✅ gate 点灯 |
| E4 historical query | 🟢 計測済 | `s9`(warm/cold) + `s14`(agg)。warm 54.7ms / **agg_hour 606ms（rollup-backed, bimodal 解消）** / agg_day cache-hit 7.6ms / multipoint 0.11(sublinear) ✅ |
| E5 pointlist integrity | 🟢 計測済 | `s10`。resolution 1.000 / unknown・ownership・remap 1.000。本評価の独自性 |
| E6 control safety | 🟢 計測済 | `s6`(rtt) + `s12`(safety)。rtt ~23ms / stale_replay 0 / not_writable 1.0 / typed_failure 1.0 ✅。offline_503・success_rate・duplicate_write は SKIP（局所再現不可/要接続GW、unit test + #186 で担保） |
| E7 storage cost | 🟢 計測済 | objects/building-hour =2 ✅ + bytes/row 比 0.0211 ✅（parquet 2.84 vs TimescaleDB 非圧縮 134.84 B/行, `measure_bytes_per_row`） |
| E8 resilience | 🟢 計測済 | `s16`（connector 停止→再起動）。data_loss_under_outage 0.0 ✅ / RTO 4.5s（report）。pure 計算 `resilience_metrics` は TDD。graceful degradation は follow-up |
