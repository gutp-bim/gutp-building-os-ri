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

## KPIs and PromQL used by the Platform UI

`/platform/status` (`GET /api/v1/system/status`, #144 / #146 / #456) shows the pipeline KPIs below
without Grafana. The API server runs these instant queries against `PROMETHEUS_URL`; the strings are
the constants on `SystemStatusService` (keep this table and that class in step). Every KPI comes from an
existing instrument or recording rule — no instrument was added for the UI, and every query aggregates
away everything but fixed-vocabulary labels (`source`, `result`), per the Cardinality Policy above.

Metric names follow the OTLP → Prometheus mapping: dots become underscores, counters gain `_total`,
and a histogram whose unit is `s` gains `_seconds` (`_bucket` / `_sum` / `_count`).

| KPI | PromQL | Warn (default → setting) |
|-----|--------|--------------------------|
| Ingress msg/s | `sum(rate(building_os_ingress_messages_total[1m]))`; tooltip: `sum by (source) (…)` | — |
| Validated msg/s | `(C + G) or C or G` where C = `sum(connector:messages_processed:rate1m)` and G = `sum(rate(building_os_ingress_messages_total{source="gateway-grpc",result="published"}[1m]))` | — |
| Rejected msg/s | `(I + D) or I or D or vector(0)` where I = `sum(rate(building_os_ingress_messages_total{result!="published"}[1m]))` and D = `sum(rate(building_os_connector_messages_processed_total{connector=~"MqttConnectorWorker\|HonoConnectorWorker",result=~"skipped\|error"}[1m]))`; tooltip: `sum by (result) (…)` of each, connector rows labelled `connector:skipped` / `connector:error` | — |
| Rejected % | rejected ÷ ingress × 100 (same snapshot; null when ingress is 0) | > 1 % → `platform.kpi.rejectedPercentWarn` |
| Event lag p95 | `histogram_quantile(0.95, sum by (le) (rate(building_os_ingress_event_lag_seconds_bucket[5m])))` | > 30 s → `platform.kpi.eventLagP95WarnSeconds` |
| Consumer lag p95 | `histogram_quantile(0.95, sum by (le) (rate(building_os_ingestion_lag_seconds_bucket[5m])))` | > 5 s → `platform.kpi.consumerLagP95WarnSeconds` |
| Parquet freshness p95 | `histogram_quantile(0.95, sum by (le) (rate(building_os_parquet_writer_freshness_lag_seconds_bucket[1h])))` | > 600 s → `platform.kpi.parquetFreshnessWarnSeconds` |
| Parquet flush stalled | `sum(increase(building_os_parquet_writer_freshness_lag_seconds_count[Ws]))` with W = max(`platform.kpi.parquetFreshnessWarnSeconds`, 900); stalled = result is 0 **and** Validated > 0 | stalled → warn (fixed; shown on the freshness card as "flush 停止の可能性") |
| Parquet dropped (15m) | `sum(increase(building_os_parquet_writer_dropped_total[15m]))` | > 0 (fixed; shown on the freshness card) |
| NATS pending | `sum(nats:jetstream_consumer_pending:max)` | > 1000 → `platform.kpi.natsPendingWarn` |
| Control req (5m) | `sum(increase(building_os_control_requests_total[5m]))` | — |

Notes:

- **Rejected is measured, never derived.** `ingress − validated` would count queue backlog as
  rejection: under load the validated side trails ingress by whatever is queued. Rejection happens in
  two places, so both are counted: an ingress transport refusing a message (`result!="published"` on
  the ingress counter), and a connector dropping an MQTT / Hono message the transport had already
  forwarded to `raw.*` as `published` — a device or point that does not resolve is
  `result="skipped"`, a processing failure `result="error"` on
  `building_os_connector_messages_processed_total`. The connector term is restricted to
  `connector=~"MqttConnectorWorker|HonoConnectorWorker"` (`ConnectorWorkerBase` tags `connector` with
  the worker's type name): those are the only connectors fed by an ingress transport. The hvac /
  bacnet / environmental / electric / behavior connectors consume `raw.*` published straight to NATS,
  which the ingress counter never saw, so their drops would inflate the percentage. Rejected % stays rejected ÷ ingress. The ingress
  rows of the breakdown use the same counter and label as `/platform/ingress-rejections`, which shows
  the cumulative `source="gateway-grpc"` counts; the status page shows rates across all sources. Which
  `result` comes from where: `bad_payload` (MQTT / AMQP), `bad_topic` (MQTT), `missing_id` /
  `unknown_point` / `gateway_mismatch` / `no_building_path` / `no_device_link` / `identity_*` /
  `publish_failed` (gRPC GatewayIngress only), `connector:skipped` / `connector:error` (MQTT / Hono
  connectors). The rejection selectors return nothing until the first rejection, so the query ends in
  `or vector(0)`: "never rejected" is a real 0 computed by Prometheus, and a null can only mean the
  query itself failed (timeout / non-2xx / bad response). The API passes that null through (UI: "—")
  rather than coercing it to 0 — a timed-out rejected query must never read as a green "0 msg/s" during
  a possible rejection storm. Rejected % is null whenever ingress or rejected is null.
- **Validated** is everything published to `building-os.validated.telemetry`: the connector recording
  rule plus the gRPC GatewayIngress path, which publishes there directly without a connector and so is
  counted only on the ingress counter (`source="gateway-grpc", result="published"`). The legacy
  `msgRate1m` field (and `/home`'s data-flow card via `OperationsController`) keeps its original,
  connector-only meaning `sum(connector:messages_processed:rate1m)`; the status page reads
  `validatedRate1m`.
- **Two-sided sums are written `(A + B) or A or B`.** `A + B` alone is empty whenever one side has no
  series (no gRPC gateway deployed, no connector drop ever), which would null the KPI. The `or`
  fallbacks keep whichever side exists; with neither present the result stays empty and the API
  reports null, like every other KPI (`(A or vector(0)) + …` would report 0 with no data at all).
- **Histogram buckets.** Every `building_os` histogram whose unit is `s`
  (`building_os.ingestion.lag`, `building_os.ingress.event_lag`,
  `building_os.parquet_writer.freshness_lag`) is exported with explicit second-scale boundaries
  `0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 2.5, 5, 10, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200` (+Inf) — a view
  registered in `OtelSetup.AddBuildingOsHistogramViews`. The SDK default boundaries
  (`0, 5, 10, 25, 50, 75, 100, 250, …`) are sized for milliseconds: with them an 80 ms lag falls in
  the `(0, 5]` bucket and `histogram_quantile` reports p95 ≈ 4.75 s. Millisecond histograms
  (`*.duration`, unit `ms`) keep the defaults. A quantile is only as precise as its bucket, and
  `histogram_quantile` never returns more than the top finite bound, so anything above 7200 s reads as
  7200 s. Because the UI warns only when value > threshold, **a seconds warn threshold ≥ 7200 can never
  trigger** — keep the `platform.kpi.*Seconds` settings below it (a unit test pins every default below
  the top bucket).
- **Parquet freshness** is recorded only when the writer flushes, so it uses a 1 h window to hold
  several flushes at any realistic `PARQUET_FLUSH_INTERVAL` (default 5 min). The API server cannot see
  that ConnectorWorker variable, so the warn threshold is a setting: keep it near
  `PARQUET_FLUSH_INTERVAL` (minutes) × 2 × 60.
- **Parquet flush stall.** A writer that stops flushing records no freshness samples, so its p95 just
  goes null — on its own that is indistinguishable from "Prometheus not wired". `parquetFlushStalled`
  is true when the writer's flush counter (the freshness histogram's `_count`) exists in the window
  but did not increase over max(freshness warn threshold, 15 min) while Validated > 0; the card then
  warns with "flush 停止の可能性". No `_count` series (the writer never flushed, or timescale mode) or
  no validated traffic is not a stall; without Prometheus the field is null and the card stays "—".
- **NATS pending threshold (default 1000).** The KPI sums `jetstream_consumer_num_pending` over
  every consumer: messages in the stream **not yet delivered** to that consumer. It is not
  `num_ack_pending` (delivered, not yet acked). The distinction matters for `ParquetLakeWriterWorker`.
  It keeps fetching (≤ 1,000 msgs per pull, ≤ 20 s poll) and holds rows un-acked until the next
  flush (default every 5 min), so a whole flush window of messages is `ack_pending`, not
  `num_pending`, and does not count here. Its `MaxAckPending` is ≥ 100,000, and `FlushMaxRows`
  (50,000) flushes before it is reached, so delivery is not throttled in normal operation. The raw
  connectors (`NatsMessageSubscription`) ack each message after handling it.
  The tail-merge reader's ephemeral ordered consumer (`NatsTailReader`) is deleted as soon as its read
  ends (≤ `TailMergeOptions.FetchTimeout`, 1 s), so it cannot sit in the sum between reads. This was
  checked against nats 2.10 with NATS.Client 3.0.1. KV watchers keep `num_pending` at 0.
  **Healthy is therefore ≈ 0.** The 73 h E10 soak (1,865 points / 300 s ≈ 6.2 msg/s,
  [E10 実施記録: 73h](../../e2e/scenarios/E10-endurance-soak.md)) peaked at 2, and the earlier E10 run
  in [evaluation-report.md](../../e2e/evaluation-report.md) stayed at 0. A non-zero value is a
  backlog: a consumer is stalled or slower than ingest, the writer is catching up after a restart, or
  the writer loop is paused during a slow flush PUT.
  The default 1000 is **≈ 160 s of backlog at that reference rate**, far above the healthy noise.
  It also stays above what one slow flush can pause (≈ 6 msg/s × a few seconds). Sizing rule:
  **warn = ingest msg/s × tolerable backlog seconds.** Read ingest msg/s from Validated on the same
  page. A 60–180 s backlog is a reasonable band. Example: a site at 50 msg/s that wants the same
  ≈ 160 s would set about 8,000. The sum covers all consumers, so one stalled consumer is enough to
  cross it. Tune it in `/platform/settings`.
- `histogram_quantile` over a window with no observations returns NaN; the API maps NaN / ±Inf to
  null, which the UI shows as "—".
- The warn thresholds are `SettingsRegistry` keys (Number, category `platform`), editable in
  `/platform/settings`. The status response carries their effective values (`thresholds`); if the
  settings store (PostgreSQL) is unreachable the defaults are used, so the page still renders.
- Without Prometheus (the default OSS compose), every KPI is null and the page shows the "enable the
  observability profile" empty state. Without the NATS exporter only NATS pending is null.
- **Grafana links** appear per KPI only when `NEXT_PUBLIC_GRAFANA_URL` is set. Only Validated has a
  matching panel (Building OS Overview → Connector Processing Rate, `viewPanel=5`; it shows the
  connector side only, not gRPC GatewayIngress). The other KPIs link
  to the Building OS Overview dashboard because no dedicated panel exists for them yet.

**When a KPI goes bad: who does what.** Triage starts at the
[incident runbook](oss-incident-runbook.md) §0.

| KPI | Who | First action |
|-----|-----|--------------|
| Ingress drops to 0 | Platform operator | Check ingress transports and gateway connectivity ([runbook §5](oss-incident-runbook.md#5-ゲートウェイの大量切断), §1 if NATS is down). |
| Validated ≪ Ingress (sustained) | Platform operator | Read Consumer lag and NATS pending: backlog means connectors are behind; check ConnectorWorker readiness ([runbook §0](oss-incident-runbook.md#0-最初に見るところトリアージ)). |
| Rejected % high | Twin / gateway owner | Use the tooltip's `result` breakdown and `/platform/ingress-rejections`. `unknown_point` / `gateway_mismatch` / `no_building_path` (gRPC gateways) or `connector:skipped` (MQTT / Hono devices whose device or point does not resolve) mean fix the twin or point list; `bad_payload` / `bad_topic` mean fix the device or gateway payload; `connector:error` means read the ConnectorWorker logs. |
| Event lag high, Consumer lag normal | Gateway / device / network owner | The delay is upstream of Building OS. Check gateway buffers, device clocks, and the network. Read `timestamp_fallbacks` next to it ([oss-sla-freshness.md](oss-sla-freshness.md) §5–§6). |
| Event lag high, Consumer lag high | Platform operator | Suspect internal queueing or backpressure. Check NATS pending and ConnectorWorker capacity ([runbook §1](oss-incident-runbook.md#1-nats-が落ちた), [oss-sla-freshness.md](oss-sla-freshness.md) §6). |
| Parquet freshness high / dropped > 0 / "flush 停止の可能性" | Platform operator | Check the lake writer and MinIO (a stall means no flush at all in the window — check ConnectorWorker `lake` role readiness and its logs first) ([runbook §2](oss-incident-runbook.md#2-minioparquet-レイクが落ちた)). Dropped rows mean a producer sent unparseable timestamps, so find the source in Loki. |
| NATS pending high | Platform operator | Consumers are behind. Check ConnectorWorker replicas and readiness, then NATS ([runbook §1](oss-incident-runbook.md#1-nats-が落ちた)). |
| Control req abnormal | Control owner | Check the control audit (`point_control_audit`) and gateway egress connectivity; offline gateways fail fast with 503 ([runbook §5](oss-incident-runbook.md#5-ゲートウェイの大量切断)). |

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
