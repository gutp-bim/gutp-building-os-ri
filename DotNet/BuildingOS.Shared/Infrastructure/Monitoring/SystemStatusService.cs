using System.Globalization;
using BuildingOS.Shared.Domain.Configuration;

namespace BuildingOS.Shared.Infrastructure.Monitoring;

/// <summary>
/// Default <see cref="ISystemStatusService"/>. Service up/down comes from an HTTP <c>/health</c>
/// fan-out (<see cref="IServiceHealthProbe"/>) so it works even without Prometheus; only the KPIs
/// come from Prometheus and degrade to null when it is unconfigured. The API server itself is
/// always reported up — it is answering this very request.
/// </summary>
public sealed class SystemStatusService : ISystemStatusService
{
    /// <summary>Display name for the API server's own (always-up) entry.</summary>
    public const string SelfJob = "building-os-api";

    // KPI queries. Kept as constants so tests can assert against them and operators can tune them.
    // Recording-rule names come from oss-stack/prometheus/recording_rules.yml. Raw instrument names
    // follow the OTLP→Prometheus mapping (see BuildingOsMetrics): dots → underscores, counters gain
    // `_total`, histograms with unit "s" gain `_seconds` (+ `_bucket`/`_sum`/`_count`).
    // Every query aggregates away all labels except fixed-vocabulary ones (source / result) — never
    // point_id / device_id (observability-baseline.md §Cardinality Policy, ADR-0007).
    // docs/operations/observability-baseline.md §"KPIs and PromQL used by the Platform UI" mirrors
    // this list; keep the two in step.

    /// <summary>
    /// Connector-published msg/s (recording rule, result="published"). The legacy <c>msgRate1m</c>
    /// field and <c>/home</c>'s data-flow card (<c>OperationsController</c>) use this; it does NOT
    /// include the gRPC GatewayIngress path — see <see cref="ValidatedRate1mQuery"/>.
    /// </summary>
    public const string MsgRate1mQuery = "sum(connector:messages_processed:rate1m)";

    // Two-sided sums below are written `(A + B) or A or B`: `A + B` alone is empty when either side has
    // no series (no gRPC gateway deployed, no connector ever dropped anything), which would null the
    // whole KPI. The `or` fallbacks keep whichever side exists, and the result is empty (→ null) only
    // when neither does — the same "no data → null" semantics as every other KPI (unlike
    // `(A or vector(0)) + (B or vector(0))`, which would report 0 with no data at all).

    private const string GatewayGrpcPublishedRate =
        "sum(rate(building_os_ingress_messages_total{source=\"gateway-grpc\",result=\"published\"}[1m]))";

    /// <summary>
    /// Validated msg/s: everything published to <c>building-os.validated.telemetry</c> — the
    /// connectors (MQTT / Hono, <see cref="MsgRate1mQuery"/>) plus the gRPC GatewayIngress, which
    /// publishes there directly without a connector and counts only on the ingress counter.
    /// </summary>
    public const string ValidatedRate1mQuery =
        $"({MsgRate1mQuery} + {GatewayGrpcPublishedRate}) or {MsgRate1mQuery} or {GatewayGrpcPublishedRate}";

    /// <summary>Control requests handled in the last 5 minutes.</summary>
    public const string ControlReq5mQuery = "sum(increase(building_os_control_requests_total[5m]))";

    /// <summary>Messages received by the ingress transports (mqtt / amqp / gateway-grpc), all results.</summary>
    public const string IngressRate1mQuery = "sum(rate(building_os_ingress_messages_total[1m]))";

    /// <summary>Ingress msg/s per source — tooltip breakdown.</summary>
    public const string IngressBySourceQuery =
        "sum by (source) (rate(building_os_ingress_messages_total[1m]))";

    private const string IngressRejectedRate =
        "sum(rate(building_os_ingress_messages_total{result!=\"published\"}[1m]))";

    /// <summary>
    /// The connectors whose input the ingress counter already counted: <c>ConnectorWorkerBase</c> tags
    /// <c>connector</c> with the worker's type name, and only the MQTT / Hono connectors consume what
    /// <c>MqttIngressWorker</c> / <c>AmqpIngressWorker</c> forwarded to <c>raw.*</c>. The hvac / bacnet /
    /// environmental / electric / behavior connectors are fed straight from NATS and are not in the
    /// ingress denominator, so their drops must not count toward rejected %.
    /// </summary>
    public const string IngressFedConnectorSelector = "connector=~\"MqttConnectorWorker|HonoConnectorWorker\"";

    private const string ConnectorDroppedSelector =
        "building_os_connector_messages_processed_total{" + IngressFedConnectorSelector + ",result=~\"skipped|error\"}";

    private const string ConnectorDroppedRate = "sum(rate(" + ConnectorDroppedSelector + "[1m]))";

    /// <summary>
    /// Rejected msg/s, measured directly — deliberately NOT ingress − validated: under load the
    /// validated side trails ingress by the queue, and that gap is backlog, not rejection. Two
    /// counters, because rejection happens at two places: an ingress transport refusing a message
    /// (<c>result != "published"</c>), and a connector dropping an MQTT / Hono message the transport had
    /// already forwarded to <c>raw.*</c> as published (device / point unresolved → <c>skipped</c>, or
    /// <c>error</c>). Absent sides are handled as in <see cref="ValidatedRate1mQuery"/>. The rejection
    /// selectors return no series until the first rejection ever happens, so a trailing
    /// <c>or vector(0)</c> makes "never rejected" a real 0 in PromQL. That keeps a null from the client
    /// meaning only "the query failed" (timeout / non-2xx) — reported as null (UI: no data), never as a
    /// reassuring 0 during what might be a rejection storm.
    /// </summary>
    public const string RejectedRate1mQuery =
        $"({IngressRejectedRate} + {ConnectorDroppedRate}) or {IngressRejectedRate} or {ConnectorDroppedRate} or vector(0)";

    /// <summary>Ingress-refused msg/s per result (bad_payload / bad_topic / unknown_point …) — tooltip breakdown.</summary>
    public const string RejectedByResultQuery =
        "sum by (result) (rate(building_os_ingress_messages_total{result!=\"published\"}[1m]))";

    /// <summary>
    /// Connector-dropped msg/s per result (skipped / error) — merged into the rejected tooltip as
    /// <c>connector:skipped</c> / <c>connector:error</c> so they cannot be confused with an ingress result.
    /// </summary>
    public const string ConnectorDroppedByResultQuery =
        "sum by (result) (rate(" + ConnectorDroppedSelector + "[1m]))";

    /// <summary>Label prefix for connector drops in <see cref="SystemKpis.RejectedByResult"/>.</summary>
    public const string ConnectorResultPrefix = "connector:";

    /// <summary>p95 event-time lag (reading's own event time → hot store), #415/#443.</summary>
    public const string EventLagP95Query =
        "histogram_quantile(0.95, sum by (le) (rate(building_os_ingress_event_lag_seconds_bucket[5m])))";

    /// <summary>p95 JetStream consumer lag (stream timestamp → consumer dequeue), #415.</summary>
    public const string ConsumerLagP95Query =
        "histogram_quantile(0.95, sum by (le) (rate(building_os_ingestion_lag_seconds_bucket[5m])))";

    /// <summary>
    /// p95 Parquet lake freshness at flush (now − newest event time flushed). The histogram is only
    /// recorded on flush, so the window must hold several flushes at any realistic
    /// <c>PARQUET_FLUSH_INTERVAL</c> (default 5 min) — 1h. A writer that stops flushing is caught by
    /// <see cref="ParquetFlushesQuery"/>, not by this p95 (which just runs out of samples → null).
    /// </summary>
    public const string ParquetFreshnessP95Query =
        "histogram_quantile(0.95, sum by (le) (rate(building_os_parquet_writer_freshness_lag_seconds_bucket[1h])))";

    /// <summary>Shortest stall-detection window: 15 minutes.</summary>
    public const double MinParquetStallWindowSeconds = 900;

    /// <summary>
    /// Flushes in the stall window = max(<paramref name="freshnessWarnSeconds"/>, 15m). Returns a value
    /// only when the writer's <c>_count</c> series exists in the window (a writer that never flushed, or
    /// timescale mode, returns nothing). 0 with validated traffic flowing = the writer has stopped
    /// flushing (<see cref="SystemKpis.ParquetFlushStalled"/>).
    /// </summary>
    public static string ParquetFlushesQuery(double freshnessWarnSeconds)
    {
        var window = (long)Math.Ceiling(Math.Max(freshnessWarnSeconds, MinParquetStallWindowSeconds));
        return string.Create(CultureInfo.InvariantCulture,
            $"sum(increase(building_os_parquet_writer_freshness_lag_seconds_count[{window}s]))");
    }

    /// <summary>Rows the Parquet writer dropped (unparseable timestamp) in the last 15 minutes.</summary>
    public const string ParquetDropped15mQuery =
        "sum(increase(building_os_parquet_writer_dropped_total[15m]))";

    /// <summary>NATS JetStream pending across all consumers (recording rule over prometheus-nats-exporter, #535).</summary>
    public const string NatsPendingQuery = "sum(nats:jetstream_consumer_pending:max)";

    /// <summary>Every PromQL string this service issues (for policy tests / docs).</summary>
    public static readonly IReadOnlyList<string> AllKpiQueries =
    [
        MsgRate1mQuery, ValidatedRate1mQuery, ControlReq5mQuery, IngressRate1mQuery, IngressBySourceQuery,
        RejectedRate1mQuery, RejectedByResultQuery, ConnectorDroppedByResultQuery, EventLagP95Query, ConsumerLagP95Query, ParquetFreshnessP95Query,
        ParquetDropped15mQuery, NatsPendingQuery,
        ParquetFlushesQuery(PipelineKpiThresholds.Defaults.ParquetFreshnessWarnSeconds),
    ];

    private readonly IServiceHealthProbe _healthProbe;
    private readonly IPrometheusQueryClient _prometheus;

    public SystemStatusService(IServiceHealthProbe healthProbe, IPrometheusQueryClient prometheus)
    {
        _healthProbe = healthProbe;
        _prometheus = prometheus;
    }

    /// <summary>Status with the registry-default thresholds (stall window from the default freshness threshold).</summary>
    public Task<SystemStatus> GetStatusAsync(CancellationToken ct) =>
        GetStatusAsync(PipelineKpiThresholds.Defaults, ct);

    public async Task<SystemStatus> GetStatusAsync(PipelineKpiThresholds thresholds, CancellationToken ct)
    {
        // Health fan-out and KPI queries are independent — run them concurrently. Each already
        // degrades on its own failure (probe → "down", Prometheus → null).
        var probeTask = _healthProbe.ProbeAllAsync(ct);
        var msgRateTask = ScalarAsync(MsgRate1mQuery, ct);
        var validatedTask = ScalarAsync(ValidatedRate1mQuery, ct);
        var controlReqTask = ScalarAsync(ControlReq5mQuery, ct);
        var ingressTask = ScalarAsync(IngressRate1mQuery, ct);
        var ingressBySourceTask = _prometheus.QueryVectorAsync(IngressBySourceQuery, ct);
        var rejectedTask = ScalarAsync(RejectedRate1mQuery, ct);
        var rejectedByResultTask = _prometheus.QueryVectorAsync(RejectedByResultQuery, ct);
        var connectorDroppedTask = _prometheus.QueryVectorAsync(ConnectorDroppedByResultQuery, ct);
        var eventLagTask = ScalarAsync(EventLagP95Query, ct);
        var consumerLagTask = ScalarAsync(ConsumerLagP95Query, ct);
        var freshnessTask = ScalarAsync(ParquetFreshnessP95Query, ct);
        var droppedTask = ScalarAsync(ParquetDropped15mQuery, ct);
        var natsPendingTask = ScalarAsync(NatsPendingQuery, ct);
        var flushesTask = ScalarAsync(ParquetFlushesQuery(thresholds.ParquetFreshnessWarnSeconds), ct);
        await Task.WhenAll(
            probeTask, msgRateTask, validatedTask, controlReqTask, ingressTask, ingressBySourceTask, rejectedTask,
            rejectedByResultTask, connectorDroppedTask, eventLagTask, consumerLagTask, freshnessTask, droppedTask,
            natsPendingTask, flushesTask).ConfigureAwait(false);

        // Self is always up; add probed services, de-duplicate by name (self wins), then sort so
        // the list has a stable lexicographic order regardless of insertion order.
        var services = new List<ServiceStatus> { new(SelfJob, "up") };
        services.AddRange(await probeTask.ConfigureAwait(false));
        var ordered = services
            .GroupBy(s => s.Name, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(s => s.Name, StringComparer.Ordinal)
            .ToList();

        var ingress = await ingressTask.ConfigureAwait(false);
        var validated = await validatedTask.ConfigureAwait(false);
        var rejected = await rejectedTask.ConfigureAwait(false);

        return new SystemStatus(
            Services: ordered,
            Kpis: new SystemKpis(
                MsgRate1m: await msgRateTask.ConfigureAwait(false),
                ControlReq5m: await controlReqTask.ConfigureAwait(false),
                IngressRate1m: ingress,
                IngressBySource: Breakdown(await ingressBySourceTask.ConfigureAwait(false), "source"),
                ValidatedRate1m: validated,
                RejectedRate1m: rejected,
                RejectedPercent: Percent(rejected, ingress),
                RejectedByResult: Breakdown(
                    await rejectedByResultTask.ConfigureAwait(false), "result",
                    await connectorDroppedTask.ConfigureAwait(false), ConnectorResultPrefix),
                EventLagP95Seconds: await eventLagTask.ConfigureAwait(false),
                ConsumerLagP95Seconds: await consumerLagTask.ConfigureAwait(false),
                ParquetFreshnessP95Seconds: await freshnessTask.ConfigureAwait(false),
                ParquetDropped15m: await droppedTask.ConfigureAwait(false),
                NatsPending: await natsPendingTask.ConfigureAwait(false),
                ParquetFlushStalled: _prometheus.IsConfigured
                    ? FlushStalled(await flushesTask.ConfigureAwait(false), validated)
                    : null),
            MetricsAvailable: _prometheus.IsConfigured);
    }

    /// <summary>
    /// Scalar query with NaN/±Inf mapped to null. <c>histogram_quantile</c> over a window with no
    /// observations yields NaN, which means "no data" — and System.Text.Json cannot serialize it.
    /// </summary>
    private async Task<double?> ScalarAsync(string query, CancellationToken ct)
    {
        var v = await _prometheus.QueryScalarAsync(query, ct).ConfigureAwait(false);
        return v is { } d && double.IsFinite(d) ? d : null;
    }

    /// <summary>
    /// Stalled = the writer's flush counter exists in the window but did not move, while telemetry is
    /// still being validated. No counter (never flushed / timescale mode) or no traffic is not a stall.
    /// </summary>
    private static bool FlushStalled(double? flushes, double? validated) =>
        flushes is 0 && validated is > 0;

    private static double? Percent(double? rejected, double? ingress) =>
        rejected is { } r && ingress is { } i && i > 0 ? r / i * 100d : null;

    private static IReadOnlyList<KpiBreakdownItem> Breakdown(
        IReadOnlyList<PrometheusSample> samples, string label,
        IReadOnlyList<PrometheusSample>? prefixedSamples = null, string prefix = "") =>
        samples
            .Where(s => double.IsFinite(s.Value))
            .Select(s => new KpiBreakdownItem(s.Labels.GetValueOrDefault(label, "unknown"), s.Value))
            .Concat((prefixedSamples ?? [])
                .Where(s => double.IsFinite(s.Value))
                .Select(s => new KpiBreakdownItem(prefix + s.Labels.GetValueOrDefault(label, "unknown"), s.Value)))
            .OrderByDescending(b => b.Value)
            .ThenBy(b => b.Label, StringComparer.Ordinal)
            .ToList();
}
