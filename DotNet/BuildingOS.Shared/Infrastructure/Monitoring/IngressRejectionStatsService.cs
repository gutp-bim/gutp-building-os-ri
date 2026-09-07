namespace BuildingOS.Shared.Infrastructure.Monitoring;

/// <summary>
/// Default <see cref="IIngressRejectionStatsService"/>. Queries the same
/// <c>building_os.ingress.messages</c> counter (source=gateway-grpc) the GatewayIngress accept/
/// reject policy already increments per outcome, grouped by the <c>result</c> label. Degrades to
/// null/empty like <see cref="SystemStatusService"/> when Prometheus is unconfigured.
/// </summary>
public sealed class IngressRejectionStatsService : IIngressRejectionStatsService
{
    /// <summary>Kept as a constant so tests can assert against it and operators can tune it.</summary>
    public const string RejectionsByReasonQuery =
        "sum by (result) (building_os_ingress_messages_total{source=\"gateway-grpc\"})";

    /// <summary>The one <c>result</c> label value that is not a rejection.</summary>
    private const string AcceptedResult = "published";

    private readonly IPrometheusQueryClient _prometheus;

    public IngressRejectionStatsService(IPrometheusQueryClient prometheus) => _prometheus = prometheus;

    public async Task<IngressRejectionStats> GetAsync(CancellationToken ct)
    {
        var samples = await _prometheus.QueryVectorAsync(RejectionsByReasonQuery, ct).ConfigureAwait(false);

        var rejections = samples
            .Where(s => s.Labels.GetValueOrDefault("result") != AcceptedResult)
            .Select(s => new IngressRejectionCount(
                s.Labels.GetValueOrDefault("result", "unknown"),
                Math.Max(0L, (long)Math.Round(s.Value, MidpointRounding.AwayFromZero))))
            .OrderByDescending(r => r.Count)
            .ThenBy(r => r.Reason, StringComparer.Ordinal)
            .ToList();

        return new IngressRejectionStats(rejections, _prometheus.IsConfigured);
    }
}
