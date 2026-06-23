using System.Text;
using NATS.Client.Core;

namespace BuildingOS.Shared.Infrastructure.ControlRouting;

/// <summary>
/// Publishes a per-gateway point-list-changed signal (#224/push) onto the egress notification subject
/// so the GatewayBridge replica holding the gateway's stream can forward it down. The signal is an
/// invalidation hint; <c>revision</c> may be empty (the gateway then revalidates via ETag).
/// </summary>
public interface IPointListUpdatePublisher
{
    Task PublishAsync(string gatewayId, string revision, CancellationToken cancellationToken = default);
}

/// <summary>Core-NATS implementation publishing to <see cref="EgressSubjects.PointListUpdate"/>.</summary>
public sealed class NatsPointListUpdatePublisher(INatsConnection nats) : IPointListUpdatePublisher
{
    public async Task PublishAsync(string gatewayId, string revision, CancellationToken cancellationToken = default)
    {
        var bytes = Encoding.UTF8.GetBytes(revision ?? string.Empty);
        await nats.PublishAsync(EgressSubjects.PointListUpdate(gatewayId), bytes, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
    }
}
