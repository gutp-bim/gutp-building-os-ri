# E2E 性能評価・品質チェックレポートテンプレート

このテンプレートは
[`e2e-performance-quality-test-plan.md`](../project/e2e-performance-quality-test-plan.md)
の実行結果をまとめるためのものです。自動生成時は `metadata.json`、
各 runner の summary、Prometheus query 結果、parity-harness 結果から埋めます。

## 1. Summary

| Item | Value |
|---|---|
| Test run ID | `<test_run_id>` |
| Environment | `<local/staging/production-like>` |
| Git commit | `<commit_sha>` |
| Helm chart version | `<chart_version>` |
| Start time | `<iso8601>` |
| End time | `<iso8601>` |
| Overall result | `<PASS/FAIL/WARN>` |

## 2. Scope

| Scenario | Result | Notes |
|---|---|---|
| S1 Smoke E2E | `<PASS/FAIL/SKIP>` | |
| S2 Baseline Throughput | `<PASS/FAIL/SKIP>` | |
| S3 Burst And Backpressure | `<PASS/FAIL/SKIP>` | |
| S4 Data Size And Schema Quality | `<PASS/FAIL/SKIP>` | |
| S5 API Read Path Performance | `<PASS/FAIL/SKIP>` | |
| S6 Point Control E2E | `<PASS/FAIL/SKIP>` | |
| S7 Resilience And Replay | `<PASS/FAIL/SKIP>` | |
| S8 UI Journey | `<PASS/FAIL/SKIP>` | |

## 3. Quality Gate Result

| Gate | Threshold | Actual | Result |
|---|---:|---:|---|
| invalid schema rate | 0% | `<value>` | `<PASS/FAIL>` |
| loss rate | 0% | `<value>` | `<PASS/FAIL>` |
| duplicate rate | 0% | `<value>` | `<PASS/FAIL>` |
| API 5xx rate | < 0.1% | `<value>` | `<PASS/FAIL>` |
| ingest-to-query latency p95 | `<scenario threshold>` | `<value>` | `<PASS/FAIL>` |
| latest-value API p95 | < 500 ms | `<value>` | `<PASS/FAIL>` |
| range query API p95 | < 2 sec | `<value>` | `<PASS/FAIL>` |
| UI critical journey success | 100% | `<value>` | `<PASS/FAIL>` |
| replay recovery | no loss | `<value>` | `<PASS/FAIL>` |

## 4. Workload Profile

| Step | Devices | Points | Interval | Duration | Expected messages | Expected rows |
|---|---:|---:|---:|---:|---:|---:|
| `<step>` | `<n>` | `<n>` | `<sec>` | `<min>` | `<n>` | `<n>` |

## 5. E2E Latency

| Metric | p50 | p95 | p99 | Max |
|---|---:|---:|---:|---:|
| device publish -> NATS raw | `<ms>` | `<ms>` | `<ms>` | `<ms>` |
| NATS raw -> validated telemetry | `<ms>` | `<ms>` | `<ms>` | `<ms>` |
| validated telemetry -> TimescaleDB row | `<ms>` | `<ms>` | `<ms>` | `<ms>` |
| TimescaleDB row -> API response | `<ms>` | `<ms>` | `<ms>` | `<ms>` |
| device publish -> API queryable | `<ms>` | `<ms>` | `<ms>` | `<ms>` |

## 6. Data Quality

| Check | Expected | Actual | Result |
|---|---:|---:|---|
| sent messages | `<n>` | `<n>` | `<PASS/FAIL>` |
| validated messages | `<n>` | `<n>` | `<PASS/FAIL>` |
| stored rows | `<n>` | `<n>` | `<PASS/FAIL>` |
| duplicate rows | 0 | `<n>` | `<PASS/FAIL>` |
| invalid payloads | `<n>` | `<n>` | `<PASS/FAIL>` |
| DLQ messages | `<n>` | `<n>` | `<PASS/FAIL>` |
| checksum match | true | `<true/false>` | `<PASS/FAIL>` |

## 7. API Performance

| Endpoint group | Requests | Error rate | p95 | p99 |
|---|---:|---:|---:|---:|
| latest telemetry | `<n>` | `<%>` | `<ms>` | `<ms>` |
| range telemetry | `<n>` | `<%>` | `<ms>` | `<ms>` |
| hierarchy / graph | `<n>` | `<%>` | `<ms>` | `<ms>` |
| point control | `<n>` | `<%>` | `<ms>` | `<ms>` |

## 8. Resource Utilization

| Component | CPU p95 | Memory p95 | Saturation / Queue |
|---|---:|---:|---|
| MQTT/Hono | `<%>` | `<MiB>` | `<value>` |
| NATS | `<%>` | `<MiB>` | consumer lag `<n>` |
| ConnectorWorker | `<%>` | `<MiB>` | queue `<n>` |
| Telemetry writer | `<%>` | `<MiB>` | batch backlog `<n>` |
| TimescaleDB | `<%>` | `<MiB>` | disk I/O `<value>` |
| API Server | `<%>` | `<MiB>` | inflight `<n>` |

## 9. Resilience Events

| Event | Start | End | Expected behavior | Actual behavior | Result |
|---|---|---|---|---|---|
| `<fault>` | `<time>` | `<time>` | `<expected>` | `<actual>` | `<PASS/FAIL>` |

## 10. Evidence

- k6 summary: `<path/link>`
- Locust stats: `<path/link>`
- Playwright report: `<path/link>`
- parity-harness result: `<path/link>`
- Grafana dashboard snapshot: `<path/link>`
- Prometheus query export: `<path/link>`
- Logs / traces query: `<path/link>`

## 11. Findings

| Severity | Finding | Impact | Recommendation | Owner |
|---|---|---|---|---|
| `<High/Medium/Low>` | `<finding>` | `<impact>` | `<recommendation>` | `<owner>` |

## 12. Decision

| Decision | Value |
|---|---|
| Release readiness | `<Go/No-Go/Conditional>` |
| Required fixes before release | `<items>` |
| Follow-up tests | `<items>` |

