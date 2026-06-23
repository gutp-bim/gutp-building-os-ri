using BuildingOS.Shared.Infrastructure.ConnectorWorker;
using NATS.Client.Core;
using System.Text;

namespace BuildingOS.ConnectorWorker;

/// <summary>
/// Publishes validated telemetry messages to NATS JetStream.
/// </summary>
public sealed class NatsPublisher(INatsConnection nats) : INatsPublisher
{
    public async Task PublishAsync(string subject, string message, CancellationToken cancellationToken = default)
    {
        var bytes = Encoding.UTF8.GetBytes(message);
        await nats.PublishAsync(subject, bytes, cancellationToken: cancellationToken);
    }
}
