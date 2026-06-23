namespace BuildingOS.Shared.Infrastructure.Monitoring;

/// <summary>Up/down (or "unknown") state of a single scrape target / service.</summary>
public sealed record ServiceStatus(string Name, string Status);

/// <summary>
/// A small, curated set of operational KPIs for at-a-glance triage. Any value may be
/// <c>null</c> when the metrics backend is unavailable or the series has no data.
/// </summary>
public sealed record SystemKpis(double? MsgRate1m, double? ControlReq5m);

/// <summary>
/// Aggregate platform status returned by <c>GET /api/v1/system/status</c>. Built to be useful
/// even without Grafana — and to not hard-fail when Prometheus is absent
/// (<see cref="MetricsAvailable"/> is then false and KPIs are null).
/// </summary>
public sealed record SystemStatus(
    IReadOnlyList<ServiceStatus> Services,
    SystemKpis Kpis,
    bool MetricsAvailable);
