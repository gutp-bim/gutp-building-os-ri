# E2E 性能評価・品質チェックテスト計画

Building OS OSS の入力からデータ蓄積、API 取得、UI 表示までを対象にした
E2E 性能評価と品質チェックの計画です。目的は、本番導入前に「どこまでの負荷
を処理できるか」「データが欠落・重複・破損せず取得できるか」「結果を継続的に
レポート化できるか」を確認することです。

## 対象範囲

```text
Device simulator
  -> MQTT / Hono / Mosquitto
  -> NATS JetStream building-os.raw.*
  -> ConnectorWorker
  -> NATS JetStream building-os.validated.telemetry
  -> Telemetry writer
  -> TimescaleDB
  -> API Server
  -> web-client / admin-console
```

対象外:

- 実機設備の物理応答時間
- Azure 旧構成の性能評価。ただし移行比較では旧結果を baseline として扱う

## 評価観点

| 観点 | 確認内容 | 主な指標 |
|---|---|---|
| Ingest throughput | device payload を受け続けられるか | msg/s, points/s, NATS publish ack latency |
| End-to-end latency | 入力から API 取得可能になるまで | p50/p95/p99 latency |
| Data quality | 欠落・重複・schema 逸脱がないか | loss rate, duplicate rate, invalid rate |
| Storage quality | TimescaleDB に正しく保存されるか | row count, checksum, query latency |
| API quality | 取得 API が安定して応答するか | HTTP status, p95/p99 duration, error rate |
| Control plane | point-control request/result が成立するか | success rate, timeout rate, stream latency |
| Resilience | 一部コンポーネント障害から復旧できるか | recovery time, replay success rate |
| Observability | 原因調査に必要な証跡が残るか | trace_id coverage, log correlation, dashboard completeness |

## 推奨ツール構成

| 用途 | 推奨 | 理由 |
|---|---|---|
| HTTP/API 負荷 | k6 | threshold により CI で pass/fail しやすく、Prometheus remote write で Grafana に載せやすい |
| MQTT/NATS device load | Locust または Python asyncio | MQTT/NATS のような HTTP 以外の protocol を Python client で表現しやすく、分散実行しやすい |
| Browser E2E | Playwright | Next.js UI のログイン、一覧、詳細、グラフ表示を実ブラウザで確認できる |
| 統合テスト | xUnit + Testcontainers | 既存 `BuildingOS.IntegrationTest` と親和性が高い |
| 品質差分 | `Tools/parity-harness` | JSON/HTML レポート出力が既にあり、API response / golden file 差分に使える |
| レポート集約 | Allure Report + Grafana snapshot + Markdown | 自動テスト結果、時系列メトリクス、手動所見を 1 つの成果物へ集約しやすい |

既存の `Tools/workload-test-project` は Azure IoT Hub 前提の負荷試験ツールです。
OSS E2E では、この設計を流用しつつ送信先を MQTT/Hono/NATS に置き換えるか、
新規に `Tools/e2e-performance` として k6 / Locust / Python asyncio を組み合わせます。

## テストデータ設計

### Device model

| レベル | 例 | 用途 |
|---|---|---|
| Small | 10 devices, 100 points | CI smoke / local regression |
| Medium | 250 devices, 2,500 points | staging baseline |
| Large | 1,000 devices, 10,000 points | 本番想定性能評価 |
| Stress | 5,000 devices 以上 | 限界確認、capacity planning |

### Message profile

| Profile | Interval | Points/message | 目的 |
|---|---:|---:|---|
| Baseline | 60 sec | 1-10 | 通常運用の基準値 |
| Burst | 5-10 sec | 1-10 | 短時間の集中送信 |
| Wide payload | 60 sec | 50-100 | payload size と Connector 変換負荷 |
| Mixed protocol | 15-60 sec | 1-50 | HVAC/BACnet/environment/electric/behavior 混在 |

すべての message に `test_run_id`, `device_id`, `seq`, `point_id`,
`observed_at` を含めます。これにより NATS、TimescaleDB、API response を同じ
run ID で追跡し、欠落・重複・遅延を計算できます。

## シナリオ

### S1: Smoke E2E

目的: ローカルまたは PR 環境で E2E 経路が壊れていないことを短時間で確認する。

手順:

1. `docker-compose.oss.yaml` で OSS stack を起動する。
2. API Server、ConnectorWorker、Telemetry writer を起動する。
3. 10 devices × 10 minutes の MQTT payload を publish する。
4. `building-os.validated.telemetry` の publish 件数を確認する。
5. TimescaleDB の `telemetry` row count を確認する。
6. API で対象 `point_id` の最新値と範囲取得を確認する。

合格基準:

- loss rate = 0%
- duplicate rate = 0%
- invalid schema rate = 0%
- p95 ingest-to-query latency < 5 sec
- API 5xx = 0

### S2: Baseline Throughput

目的: 本番想定の標準負荷で安定稼働することを確認する。

負荷:

- 250 devices
- 2,500 points
- 60 sec interval
- 60 minutes

合格基準:

- p95 ingest-to-query latency < 10 sec
- p99 API latency < 1 sec for latest-value query
- NATS consumer lag が継続増加しない
- TimescaleDB CPU / memory / disk I/O が saturation しない

### S3: Burst And Backpressure

目的: 短時間 burst 時の遅延、retry、consumer lag、復旧時間を確認する。

負荷:

- 1,000 devices
- 10 sec interval を 15 minutes
- 5 sec interval を 15 minutes
- baseline に戻して 30 minutes

合格基準:

- burst 終了後 10 minutes 以内に NATS consumer lag が baseline に戻る
- DLQ 件数が既知の invalid payload 以外で増えない
- TimescaleDB write error = 0
- API latest-value は burst 中も p99 < 3 sec

### S4: Data Size And Schema Quality

目的: payload が大きい場合の Connector 変換と storage mapping の品質を確認する。

負荷:

- 100 devices
- 10 / 25 / 50 / 100 points per message
- 各 30 minutes

合格基準:

- `valid-message.json` schema validation success rate = 100%
- `data` JSONB が protocol-specific attribute を保持する
- TimescaleDB row count が expected points と一致する
- API response の numeric value と timestamp が input と一致する

### S5: API Read Path Performance

目的: 蓄積済みデータを API から取得するときの応答時間と正確性を確認する。

負荷:

- k6 で latest-value query、range query、building/floor/device traversal API を実行
- 10 / 50 / 100 virtual users
- 各 15 minutes

合格基準:

- latest-value p95 < 500 ms
- range query p95 < 2 sec
- error rate < 0.1%
- API response と TimescaleDB direct query の row count / min / max / avg が一致

### S6: Point Control E2E

目的: point-control request/result の end-to-end latency と timeout を確認する。

手順:

1. API から point-control request を送信する。
2. `building-os.control.request` publish を確認する。
3. `NatsPointControlWorker` の handler 実行を確認する。
4. `building-os.control.result.<controlId>` を確認する。
5. gRPC stream が client に result を返すことを確認する。

合格基準:

- success rate >= 99%
- p95 request-to-result latency < 3 sec
- timeout rate < 1%
- result と audit record が control ID で突合できる

### S7: Resilience And Replay

目的: コンポーネント停止や遅延時に、データ欠落なく復旧できることを確認する。

Fault injection:

- ConnectorWorker を 5 minutes 停止
- Telemetry writer を 5 minutes 停止
- TimescaleDB を短時間 restart
- invalid payload を 1% 混入

合格基準:

- JetStream replay により停止期間の valid message が保存される
- invalid payload は DLQ または reject として分類される
- 復旧後の row count が expected と一致する
- recovery time と lost messages が report に記録される

### S8: UI Journey

目的: 蓄積データがユーザー操作で取得・表示できることを確認する。

対象:

- Keycloak login
- buildings / floors / spaces / devices / points navigation
- latest telemetry 表示
- warm data graph 表示
- cold data download 操作
- admin-console の users / groups / permissions 表示

合格基準:

- Playwright scenario success rate = 100%
- primary pages の p95 load time < 3 sec
- API error が UI 上で未処理例外にならない

## 計測項目

### Common tags

すべての metric / log / trace に次を付与します。

| Tag | 用途 |
|---|---|
| `test_run_id` | レポート単位の相関 ID |
| `scenario` | S1-S8 のシナリオ ID |
| `step` | 負荷段階 |
| `protocol` | mqtt, hono, nats, api, grpc |
| `device_type` | hvac, bacnet, environmental, electric, behavior |
| `environment` | local, staging, production-like |

### Metrics

| Component | Metric |
|---|---|
| Device simulator | sent messages, send errors, publish latency |
| MQTT/Hono | accepted messages, auth failures, connection count |
| NATS | publish ack latency, consumer lag, redeliveries, stream bytes |
| ConnectorWorker | processed messages, invalid messages, processing latency |
| Telemetry writer | write latency, batch size, failed writes |
| TimescaleDB | insert latency, query latency, chunk size, CPU, memory, disk I/O |
| API Server | request duration, status code, exception count, auth failures |
| Frontend | page load duration, failed requests, browser console errors |

## レポート自動生成方針

### 推奨構成

1. Test runner が `results/<test_run_id>/` に machine-readable artifact を出力する。
2. k6 / Locust / Playwright / parity-harness の結果を JSON または JUnit XML に統一する。
3. Prometheus から test window の metric snapshot を取得する。
4. `report-generator` が Markdown と HTML を生成する。
5. PR / release / test evidence として artifact を保存する。

出力例:

```text
results/
  2026-05-18T120000Z-staging-baseline/
    metadata.json
    k6-summary.json
    locust-stats.csv
    playwright-results.xml
    parity-result.json
    prometheus-range-query.json
    grafana-dashboard-links.md
    report.md
    report.html
```

### Framework recommendation

- **短期**: `Tools/parity-harness` の JSON/HTML reporter を拡張し、E2E summary
  を Markdown/HTML で出す。
- **中期**: Allure Report を採用し、xUnit / Playwright / pytest / Locust の
  JUnit XML を集約する。性能 metric は Grafana dashboard link と PNG snapshot
  を添付する。
- **長期**: CI で k6 thresholds と quality gates を実行し、失敗時は PR check を
  red にする。定期性能試験は GitHub Actions self-hosted runner または Argo
  Workflow で staging に対して実行する。

参考:

- k6 thresholds: https://grafana.com/docs/k6/latest/using-k6/thresholds/
- k6 Prometheus remote write: https://grafana.com/docs/k6/latest/results-output/real-time/prometheus-remote-write/
- Locust distributed load testing: https://docs.locust.io/en/stable/running-distributed.html
- Allure Report: https://allurereport.org/docs/

## Quality Gates

| Gate | 条件 | Fail 時の扱い |
|---|---|---|
| Schema | invalid schema rate = 0% | release block |
| Loss | expected row count と actual row count が一致 | release block |
| Duplicate | duplicate rate = 0% | release block |
| Latency | scenario ごとの p95/p99 SLO を満たす | regression review |
| Error | API 5xx < 0.1%, worker unhandled exception = 0 | release block |
| Replay | worker/writer 停止後に欠落なく復旧 | release block |
| UI | critical journey success rate = 100% | release block |
| Observability | test_run_id で logs/metrics/traces を相関可能 | release block for staging |

## 実装ロードマップ

1. `test_run_id` を device payload、NATS header、API response、logs に通す。
2. MQTT/Hono/NATS 向け device load generator を整備する。
3. k6 API scenario を追加する。
4. Playwright UI journey を追加する。
5. Prometheus range query から test window の metric を取得する script を追加する。
6. `Tools/parity-harness` または新規 `Tools/e2e-report-generator` で Markdown/HTML
   report を生成する。
7. S1 smoke を CI または nightly に入れる。
8. S2-S8 を staging の定期性能評価として運用する。

## レポートテンプレート

詳細な記入項目は
[`e2e-performance-report-template.md`](../reference/e2e-performance-report-template.md) を参照してください。
