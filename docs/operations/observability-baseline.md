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

> The architectural decision behind this policy — which layer owns per-point state, and where the
> line between Alertmanager and Building OS's own alerting runs — is recorded in
> [ADR-0007](../adr/0007-observability-domain-health-boundary.md). This section stays the
> operational reference (allowed tags, targets, recording rules); the ADR carries the reasoning.

**Never use `point_id` or `device_id` as Prometheus labels.**

Reason: with 100,000+ points, each point_id label creates a separate time series.
At 100k points × 10 metrics = 1M active series → Prometheus memory usage grows to several GB.

Instead:
- Aggregate telemetry metrics at the **connector** or **building_id** level in PromQL.
- Use recording rules (`oss-stack/prometheus/recording_rules.yml`) to pre-aggregate.
- Store point-level detail in **TimescaleDB** (Warm tier) or **Parquet** (Cold tier), not in Prometheus.
- For per-point debugging, use **Loki** log lines (not labels).

**Fixed-vocabulary tags are fine.** A tag whose values come from a closed set in the code adds a
bounded number of series, unlike an identifier. The ingest signals below use only such tags:

| Metric | Tag | Values |
|--------|-----|--------|
| `building_os.ingress.messages` | `source` | `mqtt`, `amqp`, `gateway-grpc` |
| `building_os.ingress.messages` | `result` | transports: `published`, `bad_topic` (MQTT only), `bad_payload`; gRPC ingress: `published`, `missing_id`, `identity_missing`, `identity_mismatch`, `unknown_point`, `gateway_mismatch`, `no_building_path`, `no_device_link`, `publish_failed` |
| `building_os.ingress.messages` | `gateway` | gateway id, `source=gateway-grpc` only — bounded by the number of gateways, not points |
| `building_os.ingress.event_lag` | `source` | `connector`, `gateway-grpc` |
| `building_os.ingestion.lag` | `subject` | the `building-os.raw.*` subject the consumer reads |

`building_os.ingress.event_lag` deliberately carries **no** point or gateway tag: it is recorded per
telemetry entity, so either would reintroduce exactly the per-point series this section forbids.

## Ingest Lag Signals (#415)

Ingest saturation produces no errors — every reading is still accepted, no error counter moves, and
readings simply arrive later and later. Two histograms make that visible:

| Signal | What it measures |
|--------|------------------|
| `building_os.ingress.event_lag` | Seconds between a reading's own event time (the telemetry `datetime`) and its arrival in the hot store. Recorded at the point every validated-telemetry producer converges, so it covers connectors and the gRPC ingress alike. |
| `building_os.ingestion.lag` | Seconds a JetStream consumer trails its own raw-subject stream (consumer lag). |

`building_os.ingress.messages` also carries a `result` tag, so a message a transport refuses
(malformed topic or non-JSON payload) is counted rather than only logged.

Which of the two rising means what, the `building_os.ingress.timestamp_fallbacks` caveat on
`event_lag`, and how to measure a deployment's sustainable ingest rate are in
[`oss-sla-freshness.md`](oss-sla-freshness.md) §5–§6 — not duplicated here.

## Recording Rules

Pre-aggregated metrics in `oss-stack/prometheus/recording_rules.yml`:

| Rule | Purpose |
|------|---------|
| `job:http_server_requests:rate5m` | API request rate per route |
| `job:http_server_duration_p95:rate5m` | P95 latency per route |
| `job:http_server_error_rate:rate5m` | 5xx error rate |
| `connector:messages_processed:rate1m` | Telemetry ingestion rate per connector |
| `connector:validation_errors:rate1m` | Schema validation failure rate |
| `nats:jetstream_consumer_pending:max` | NATS backpressure indicator (per `stream_name`, `consumer_name`) |
| `nats:jetstream_msgs_delivered:rate1m` | JetStream delivery rate per `stream_name` (redeliveries included) |
| `nats:jetstream_consumer_redelivered:max` | Outstanding messages redelivered at least once (ack timeouts / naks) |
| `nats:jetstream_stream_bytes:max` | Stream storage footprint per `stream_name` (bounded by MaxAge / MaxBytes) |

The `nats:*` rules read the `nats` scrape job, which targets `building-os.nats-exporter:7777`
(`natsio/prometheus-nats-exporter`, `-varz -jsz=all`, observability profile). nats-server itself has
no Prometheus `/metrics` endpoint — `:8222/metrics` is 404 — so without the exporter these rules are
empty (#535). The exporter emits `jetstream_{server,stream,consumer}_*` and `gnatsd_varz_*` series,
labelled `stream_name` / `consumer_name`; the names were verified against exporter 0.20.2 and
`nats:2.10-alpine`, and `Tools/e2e-performance/tests/test_observability_config.py` pins the rules to
that verified set.

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
