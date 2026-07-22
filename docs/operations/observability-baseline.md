# Observability Baseline — Building OS OSS

This document records the observability stack configuration decisions and baseline targets.
Update this file with measured values after running E2E performance tests.

## Configuration Summary

| Component | Setting | Value |
|-----------|---------|-------|
| Prometheus | Retention | 15d local |
| Prometheus | Scrape interval | 15s |
| Loki | Default retention | 30d |
| Loki | Max labels per series | 15 |
| Tempo | Trace retention | 7d |
| Tempo | Sampling | 10% probabilistic (via OTel Collector) |

## Cardinality Policy

**Never use `point_id` or `device_id` as Prometheus labels.**

Reason: with 100,000+ points, each point_id label creates a separate time series.
At 100k points × 10 metrics = 1M active series → Prometheus memory usage grows to several GB.

Instead:
- Aggregate telemetry metrics at the **connector** or **building_id** level in PromQL.
- Use recording rules (`oss-stack/prometheus/recording_rules.yml`) to pre-aggregate.
- Store point-level detail in **TimescaleDB** (Warm tier) or **Parquet** (Cold tier), not in Prometheus.
- For per-point debugging, use **Loki** log lines (not labels).

## Recording Rules

Pre-aggregated metrics in `oss-stack/prometheus/recording_rules.yml`:

| Rule | Purpose |
|------|---------|
| `job:http_server_requests:rate5m` | API request rate per route |
| `job:http_server_duration_p95:rate5m` | P95 latency per route |
| `job:http_server_error_rate:rate5m` | 5xx error rate |
| `connector:messages_processed:rate1m` | Telemetry ingestion rate per connector |
| `connector:validation_errors:rate1m` | Schema validation failure rate |
| `nats:jetstream_consumer_pending:max` | NATS backpressure indicator |
| `nats:jetstream_msgs_delivered:rate1m` | JetStream message delivery rate |

## Grafana Dashboard Guidelines

- **Template variables**: always set `limit=100` on variable queries that enumerate devices/points.
- **Point selection**: use `connector` or `building_id` variables instead of `point_id`.
- **Heavy queries**: replace with recording rule references (prefixed `job:`, `connector:`, `nats:`).

## Baseline Targets (to be filled after E2E runs)

| Metric | Target | Measured |
|--------|--------|---------|
| Prometheus active series (steady state) | < 50,000 | — |
| Prometheus memory (steady state) | < 512 MB | — |
| Loki ingestion rate | < 1 MB/s | — |
| Loki storage (30d) | < 10 GB | — |
| Tempo storage (7d, 10% sample) | < 5 GB | — |
| Grafana dashboard p95 load time | < 2 s | — |

Update the **Measured** column after running S2/S3/S4 E2E performance tests (Issue #72).

## Minimum Profile

In the minimum profile (`docker-compose.minimal.yaml`), the observability stack is **disabled by default**.
Set `OBSERVABILITY_ENABLED=true` or use the production profile to enable Prometheus/Grafana/Loki/Tempo.
