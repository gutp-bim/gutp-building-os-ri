namespace BuildingOS.ConnectorWorker.Connectors;

/// <summary>
/// Transport for ingress telemetry: enqueues a message onto a <c>building-os.*</c> subject on the
/// ingress spine (raw or validated). Abstracted so the gRPC ingress service can be tested without a
/// live NATS server. The owning JetStream stream is resolved from the subject (see
/// <see cref="Shared.Infrastructure.Messaging.NatsStreamTopology"/>), so the same bus serves both
/// <c>building-os.raw.{protocol}</c> and <c>building-os.validated.telemetry</c>.
/// </summary>
public interface IIngressTelemetryBus
{
    /// <summary>Publishes <paramref name="message"/> to <paramref name="subject"/>, ensuring the
    /// subject's stream exists first.</summary>
    Task PublishAsync(string subject, string message, CancellationToken cancellationToken);
}
