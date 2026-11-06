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
    /// subject's stream exists first, and returns once the stream has acknowledged it.</summary>
    Task PublishAsync(string subject, string message, CancellationToken cancellationToken);

    /// <summary>
    /// Pipelined form (#625 slice B): the returned task completes once the message has been
    /// validated and handed to the transport; its result is the <b>ack task</b>, which completes once
    /// the stream has persisted the message (and faults the way <see cref="PublishAsync"/> would
    /// throw). Pre-send failures — size limits, a missing stream — throw from the outer task so the
    /// caller can classify them before anything is in flight. The default simply wraps
    /// <see cref="PublishAsync"/>, which keeps a serial transport correct under a windowed caller.
    /// </summary>
    Task<Task> BeginPublishAsync(string subject, string message, CancellationToken cancellationToken)
        => Task.FromResult(PublishAsync(subject, message, cancellationToken));
}
