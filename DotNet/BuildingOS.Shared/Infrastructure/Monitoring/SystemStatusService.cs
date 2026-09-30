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

    /// <summary>Validated msg/s: messages connectors published (recording rule, result="published").</summary>
    public const string MsgRate1mQuery = "sum(connector:messages_processed:rate1m)";

    /// <summary>Control requests handled in the last 5 minutes.</summary>
    public const string ControlReq5mQuery = "sum(increase(building_os_control_requests_total[5m]))";

    /// <summary>Messages received by the ingress transports (mqtt / amqp / gateway-grpc), all results.</summary>
    public const string IngressRate1mQuery = "sum(rate(building_os_ingress_messages_total[1m]))";

    /// <summary>Ingress msg/s per source — tooltip breakdown.</summary>
    public const string IngressBySourceQuery =
        "sum by (source) (rate(building_os_ingress_messages_total[1m]))";

    /// <summary>
    /// Rejected msg/s, measured directly on the ingress counter's <c>result</c> tag. Deliberately NOT
    /// ingress − validated: under load the validated side trails ingress by the queue, and that gap is
    /// backlog, not rejection.
    /// </summary>
    public const string RejectedRate1mQuery =
        "sum(rate(building_os_ingress_messages_total{result!=\"published\"}[1m]))";

    /// <summary>Rejected msg/s per result (bad_payload / unknown_point / …) — tooltip breakdown.</summary>
    public const string RejectedByResultQuery =
        "sum by (result) (rate(building_os_ingress_messages_total{result!=\"published\"}[1m]))";

    /// <summary>p95 event-time lag (reading's own event time → hot store), #415/#443.</summary>
    public const string EventLagP95Query =
        "histogram_quantile(0.95, sum by (le) (rate(building_os_ingress_event_lag_seconds_bucket[5m])))";

    /// <summary>p95 JetStream consumer lag (stream timestamp → consumer dequeue), #415.</summary>
    public const string ConsumerLagP95Query =
        "histogram_quantile(0.95, sum by (le) (rate(building_os_ingestion_lag_seconds_bucket[5m])))";

    /// <summary>
    /// p95 Parquet lake freshness at flush (now − newest event time flushed). 15m window so at least
    /// one flush lands in it at the default 5-minute <c>PARQUET_FLUSH_INTERVAL</c>.
    /// </summary>
    public const string ParquetFreshnessP95Query =
        "histogram_quantile(0.95, sum by (le) (rate(building_os_parquet_writer_freshness_lag_seconds_bucket[15m])))";

    /// <summary>Rows the Parquet writer dropped (unparseable timestamp) in the last 15 minutes.</summary>
    public const string ParquetDropped15mQuery =
        "sum(increase(building_os_parquet_writer_dropped_total[15m]))";

    /// <summary>NATS JetStream pending across all consumers (recording rule over prometheus-nats-exporter, #535).</summary>
    public const string NatsPendingQuery = "sum(nats:jetstream_consumer_pending:max)";

    /// <summary>Every PromQL string this service issues (for policy tests / docs).</summary>
    public static readonly IReadOnlyList<string> AllKpiQueries =
    [
        MsgRate1mQuery, ControlReq5mQuery, IngressRate1mQuery, IngressBySourceQuery, RejectedRate1mQuery,
        RejectedByResultQuery, EventLagP95Query, ConsumerLagP95Query, ParquetFreshnessP95Query,
        ParquetDropped15mQuery, NatsPendingQuery,
    ];

    private readonly IServiceHealthProbe _healthProbe;
    private readonly IPrometheusQueryClient _prometheus;

    public SystemStatusService(IServiceHealthProbe healthProbe, IPrometheusQueryClient prometheus)
    {
        _healthProbe = healthProbe;
        _prometheus = prometheus;
    }

    public async Task<SystemStatus> GetStatusAsync(CancellationToken ct)
    {
        // Health fan-out and KPI queries are independent — run them concurrently. Each already
        // degrades on its own failure (probe → "down", Prometheus → null).
        var probeTask = _healthProbe.ProbeAllAsync(ct);
        var msgRateTask = ScalarAsync(MsgRate1mQuery, ct);
        var controlReqTask = ScalarAsync(ControlReq5mQuery, ct);
        var ingressTask = ScalarAsync(IngressRate1mQuery, ct);
        var ingressBySourceTask = _prometheus.QueryVectorAsync(IngressBySourceQuery, ct);
        var rejectedTask = ScalarAsync(RejectedRate1mQuery, ct);
        var rejectedByResultTask = _prometheus.QueryVectorAsync(RejectedByResultQuery, ct);
        var eventLagTask = ScalarAsync(EventLagP95Query, ct);
        var consumerLagTask = ScalarAsync(ConsumerLagP95Query, ct);
        var freshnessTask = ScalarAsync(ParquetFreshnessP95Query, ct);
        var droppedTask = ScalarAsync(ParquetDropped15mQuery, ct);
        var natsPendingTask = ScalarAsync(NatsPendingQuery, ct);
        await Task.WhenAll(
            probeTask, msgRateTask, controlReqTask, ingressTask, ingressBySourceTask, rejectedTask,
            rejectedByResultTask, eventLagTask, consumerLagTask, freshnessTask, droppedTask,
            natsPendingTask).ConfigureAwait(false);

        // Self is always up; add probed services, de-duplicate by name (self wins), then sort so
        // the list has a stable lexicographic order regardless of insertion order.
        var services = new List<ServiceStatus> { new(SelfJob, "up") };
        services.AddRange(await probeTask.ConfigureAwait(false));
        var ordered = services
            .GroupBy(s => s.Name, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(s => s.Name, StringComparer.Ordinal)
            .ToList();

        var msgRate = await msgRateTask.ConfigureAwait(false);
        var ingress = await ingressTask.ConfigureAwait(false);
        var rejected = RejectedOrZero(await rejectedTask.ConfigureAwait(false), ingress);

        return new SystemStatus(
            Services: ordered,
            Kpis: new SystemKpis(
                MsgRate1m: msgRate,
                ControlReq5m: await controlReqTask.ConfigureAwait(false),
                IngressRate1m: ingress,
                IngressBySource: Breakdown(await ingressBySourceTask.ConfigureAwait(false), "source"),
                ValidatedRate1m: msgRate,
                RejectedRate1m: rejected,
                RejectedPercent: Percent(rejected, ingress),
                RejectedByResult: Breakdown(await rejectedByResultTask.ConfigureAwait(false), "result"),
                EventLagP95Seconds: await eventLagTask.ConfigureAwait(false),
                ConsumerLagP95Seconds: await consumerLagTask.ConfigureAwait(false),
                ParquetFreshnessP95Seconds: await freshnessTask.ConfigureAwait(false),
                ParquetDropped15m: await droppedTask.ConfigureAwait(false),
                NatsPending: await natsPendingTask.ConfigureAwait(false)),
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
    /// A <c>result!="published"</c> selector returns an empty vector until the first rejection ever
    /// happens. With ingress data present that means zero rejections, not "unknown".
    /// </summary>
    private static double? RejectedOrZero(double? rejected, double? ingress) =>
        rejected ?? (ingress is not null ? 0 : null);

    private static double? Percent(double? rejected, double? ingress) =>
        rejected is { } r && ingress is { } i && i > 0 ? r / i * 100d : null;

    private static IReadOnlyList<KpiBreakdownItem> Breakdown(IReadOnlyList<PrometheusSample> samples, string label) =>
        samples
            .Where(s => double.IsFinite(s.Value))
            .Select(s => new KpiBreakdownItem(s.Labels.GetValueOrDefault(label, "unknown"), s.Value))
            .OrderByDescending(b => b.Value)
            .ThenBy(b => b.Label, StringComparer.Ordinal)
            .ToList();
}
