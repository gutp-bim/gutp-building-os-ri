using Microsoft.Extensions.Diagnostics.HealthChecks;
using NATS.Client.Core;

namespace BuildingOS.ConnectorWorker.Infrastructure.Health;

/// <summary>
/// Readiness check (#145): the worker is ready only when its NATS connection is <c>Open</c> — i.e. it
/// can actually consume the raw/validated subjects and publish. Liveness (the process is up and the
/// HTTP listener responds) is reported separately by the check-free <c>/health/live</c> endpoint.
/// Registered with the <c>ready</c> tag so it is included by <c>/health/ready</c> (and the overall
/// <c>/health</c>), which the system-status fan-out (#144) and the orchestrator probe.
/// </summary>
public sealed class NatsReadinessHealthCheck(INatsConnection connection) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var state = connection.ConnectionState;
        return Task.FromResult(state == NatsConnectionState.Open
            ? HealthCheckResult.Healthy($"NATS connection is {state}")
            : HealthCheckResult.Unhealthy($"NATS connection is {state} (expected Open)"));
    }
}
