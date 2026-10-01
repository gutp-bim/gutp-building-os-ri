using BuildingOS.Shared.Domain.Configuration;

namespace BuildingOS.Shared.Infrastructure.Monitoring;

/// <summary>Up/down (or "unknown") state of a single scrape target / service.</summary>
public sealed record ServiceStatus(string Name, string Status);

/// <summary>One bucket of a KPI breakdown (e.g. ingress rate for <c>source=mqtt</c>).</summary>
public sealed record KpiBreakdownItem(string Label, double Value);

/// <summary>
/// A small, curated set of operational KPIs for at-a-glance triage. Any value may be
/// <c>null</c> when the metrics backend is unavailable or the series has no data.
///
/// <para>#456 added the pipeline-health KPIs. They are additive (optional, defaulted) so the original
/// two-field shape stays source- and wire-compatible. Every value comes from an existing instrument or
/// recording rule — see <c>docs/operations/observability-baseline.md</c> §"KPIs and PromQL used by the
/// Platform UI" and the query constants on <see cref="SystemStatusService"/>.</para>
/// </summary>
/// <param name="MsgRate1m">Connector-published msg/s (<c>connector:messages_processed:rate1m</c>). Kept for
/// compatibility with its original meaning; it excludes the gRPC GatewayIngress path, so on a gRPC-ingress
/// deployment it is lower than <paramref name="ValidatedRate1m"/>.</param>
/// <param name="ControlReq5m">Control requests handled in the last 5 minutes.</param>
/// <param name="IngressRate1m">Messages received by the ingress transports, all results (msg/s).</param>
/// <param name="IngressBySource">Ingress msg/s per <c>source</c> (tooltip breakdown). <see cref="SystemStatusService"/>
/// always sets it (empty without data); null only on the legacy two-field construction.</param>
/// <param name="ValidatedRate1m">Messages published to the validated subject (msg/s): connectors plus the
/// gRPC GatewayIngress (<c>source="gateway-grpc", result="published"</c>), which publishes there directly.</param>
/// <param name="RejectedRate1m">Rejected msg/s: ingress messages with <c>result != "published"</c> plus
/// connector drops (<c>result=~"skipped|error"</c>, e.g. an MQTT / Hono device or point that does not
/// resolve) — measured directly, never derived as ingress − validated (that gap is queue backlog, not rejection).</param>
/// <param name="RejectedPercent">rejected / ingress × 100; null when there is no ingress traffic.</param>
/// <param name="RejectedByResult">Rejected msg/s per <c>result</c> (tooltip breakdown); connector drops are
/// labelled <c>connector:skipped</c> / <c>connector:error</c>.</param>
/// <param name="EventLagP95Seconds">p95 of <c>building_os.ingress.event_lag</c> (event time → hot store).</param>
/// <param name="ConsumerLagP95Seconds">p95 of <c>building_os.ingestion.lag</c> (JetStream consumer lag).</param>
/// <param name="ParquetFreshnessP95Seconds">p95 of <c>building_os.parquet_writer.freshness_lag</c> over 1h.</param>
/// <param name="ParquetDropped15m">Rows the Parquet writer dropped in the last 15 minutes (&gt; 0 = warn).</param>
/// <param name="NatsPending">Sum of <c>nats:jetstream_consumer_pending:max</c> (null without the NATS exporter).</param>
/// <param name="ParquetFlushStalled">True when the Parquet writer's flush counter exists but did not move in
/// max(freshness warn threshold, 15m) while validated telemetry is flowing — the writer has likely stopped
/// flushing (its freshness p95 then has no samples and reads null). False otherwise; null without Prometheus.</param>
public sealed record SystemKpis(
    double? MsgRate1m,
    double? ControlReq5m,
    double? IngressRate1m = null,
    IReadOnlyList<KpiBreakdownItem>? IngressBySource = null,
    double? ValidatedRate1m = null,
    double? RejectedRate1m = null,
    double? RejectedPercent = null,
    IReadOnlyList<KpiBreakdownItem>? RejectedByResult = null,
    double? EventLagP95Seconds = null,
    double? ConsumerLagP95Seconds = null,
    double? ParquetFreshnessP95Seconds = null,
    double? ParquetDropped15m = null,
    double? NatsPending = null,
    bool? ParquetFlushStalled = null);

/// <summary>
/// Aggregate platform status returned by <c>GET /api/v1/system/status</c>. Built to be useful
/// even without Grafana — and to not hard-fail when Prometheus is unconfigured or unreachable
/// (<see cref="MetricsAvailable"/> is then false and KPIs are null).
/// </summary>
/// <param name="Thresholds">The effective pipeline KPI warn thresholds (#456, editable in
/// /platform/settings). Attached by the API controller; null only from callers that do not resolve settings.</param>
public sealed record SystemStatus(
    IReadOnlyList<ServiceStatus> Services,
    SystemKpis Kpis,
    bool MetricsAvailable,
    PipelineKpiThresholds? Thresholds = null);
