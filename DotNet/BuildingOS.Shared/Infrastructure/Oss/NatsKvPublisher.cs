using BuildingOS.Shared.Infrastructure.ConnectorWorker;
using BuildingOS.Shared.Infrastructure.Telemetry;
using Microsoft.Extensions.Logging;

namespace BuildingOS.Shared.Infrastructure.Oss;

/// <summary>
/// INatsPublisher decorator that additionally writes to NATS KV latest store
/// for validated telemetry publishes (subject = building-os.validated.telemetry).
/// Wraps the inner publisher and catches KV errors so they never fail the publish.
/// </summary>
public sealed class NatsKvPublisher(
    INatsPublisher inner,
    IHotTelemetryStore hot,
    ILogger<NatsKvPublisher> logger) : INatsPublisher
{
    private const string ValidatedSubject = "building-os.validated.telemetry";

    /// <summary>#415: event-lag provenance — every connector-normalised publish routes through here.</summary>
    private const string LagSource = "connector";

    public async Task PublishAsync(string subject, string message, CancellationToken cancellationToken = default)
    {
        await inner.PublishAsync(subject, message, cancellationToken);

        if (subject != ValidatedSubject) return;
        await ValidatedTelemetryHotStore.WriteAsync(hot, message, LagSource, logger, cancellationToken);
    }
}
