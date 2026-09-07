namespace BuildingOS.Shared.Infrastructure.Monitoring;

/// <summary>A single ingress-rejection reason (the metric's <c>result</c> label) and its count.</summary>
public sealed record IngressRejectionCount(string Reason, long Count);

/// <summary>
/// Ingress-rejection counts by reason (#292), for the admin-facing visibility panel. Built to
/// degrade gracefully — <see cref="MetricsAvailable"/> is false and <see cref="Rejections"/> is
/// empty when Prometheus is unconfigured, matching <see cref="SystemStatus"/>.
/// </summary>
public sealed record IngressRejectionStats(
    IReadOnlyList<IngressRejectionCount> Rejections,
    bool MetricsAvailable);
