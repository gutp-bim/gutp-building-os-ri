# E3 — 最新値取得（Hot layer）

## 目的
Hot 層（NATS KV `telemetry-latest`、point ごとに最新1件）を履歴 DB から分離することで、保存方式に
依存せず監視 UI 向けの最新値取得を低遅延に維持できることを示す。

## 計測指標
- latest read p50/p95/p99: `GET /telemetries/hot` または `/telemetries/query?latest=true`。
- latest freshness p95: 実測値発生 → 最新値 API 反映までの遅延。
- hot update success rate（NATS KV 更新成功率）。
- stale latest ratio: 一定時間以上更新されない point の割合。
- dashboard refresh latency（任意, Playwright `s8_ui.sh`）。

## 手順
1. 定常負荷（medium/large/stress）を流しつつ k6 で latest API を叩く（`k6/s5_api_read.js`,
   `k6/s9_warm_kpi.js` の `kpi_latest`）。
2. 既知 point に時刻印した値を投入し、latest API に出るまでの遅延を計測（freshness）。
3. 全 point の最新 ts をスナップショットし stale 比率を算出。

## 合否（kpi-thresholds.yaml: E3_latest_value）
latest API p95 < 500ms（stretch < 20ms）/ freshness p95 < 2000ms / stale ratio < 1%。

## 既存資産・ギャップ
- 既存: `k6/s5_api_read.js`, `k6/s9_warm_kpi.js`(`kpi_latest`), `s8_ui.sh`。
- **ギャップ**: freshness lag・stale-latest 比率の算出スクリプト。
