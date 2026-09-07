namespace BuildingOS.Shared.Infrastructure.Monitoring;

/// <summary>
/// Aggregates gRPC gateway-ingress rejection counts by reason (#292: admin-visible exposure of
/// the accept/reject hierarchy policy, which otherwise only surfaces in Prometheus/logs).
/// </summary>
public interface IIngressRejectionStatsService
{
    Task<IngressRejectionStats> GetAsync(CancellationToken ct);
}
