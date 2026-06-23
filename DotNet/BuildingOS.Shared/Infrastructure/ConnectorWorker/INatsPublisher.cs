namespace BuildingOS.Shared.Infrastructure.ConnectorWorker;

/// <summary>
/// Abstraction for publishing validated telemetry messages to NATS JetStream.
/// </summary>
public interface INatsPublisher
{
    Task PublishAsync(string subject, string message, CancellationToken cancellationToken = default);
}
