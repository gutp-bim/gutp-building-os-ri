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
    // Recording-rule names come from oss-stack/prometheus/recording_rules.yml.
    public const string MsgRate1mQuery = "sum(connector:messages_processed:rate1m)";
    public const string ControlReq5mQuery = "sum(increase(buildingos_control_requests_total[5m]))";

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
        var msgRateTask = _prometheus.QueryScalarAsync(MsgRate1mQuery, ct);
        var controlReqTask = _prometheus.QueryScalarAsync(ControlReq5mQuery, ct);
        await Task.WhenAll(probeTask, msgRateTask, controlReqTask).ConfigureAwait(false);

        // Self is always up; add probed services, de-duplicate by name (self wins), then sort so
        // the list has a stable lexicographic order regardless of insertion order.
        var services = new List<ServiceStatus> { new(SelfJob, "up") };
        services.AddRange(await probeTask.ConfigureAwait(false));
        var ordered = services
            .GroupBy(s => s.Name, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(s => s.Name, StringComparer.Ordinal)
            .ToList();

        return new SystemStatus(
            Services: ordered,
            Kpis: new SystemKpis(
                MsgRate1m: await msgRateTask.ConfigureAwait(false),
                ControlReq5m: await controlReqTask.ConfigureAwait(false)),
            MetricsAvailable: _prometheus.IsConfigured);
    }
}
