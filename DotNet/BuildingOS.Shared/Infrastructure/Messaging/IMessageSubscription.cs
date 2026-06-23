namespace BuildingOS.Shared.Infrastructure.Messaging;

/// <summary>
/// Abstracts message subscription over any transport (NATS JetStream, in-process test double).
///
/// Contract:
/// - Handlers registered via <see cref="Register"/> are dispatched in parallel (Task.WhenAll) per message.
///   Order of execution across handlers is not guaranteed.
/// - Delivery is at-most-once: the transport acknowledges the message after all handlers complete,
///   regardless of individual handler outcomes. One lost sensor reading is acceptable;
///   redelivery loops are not. See docs/adr/0001-at-most-once-connector-delivery.md.
/// </summary>
public interface IMessageSubscription
{
    /// <summary>
    /// Registers a message handler. Multiple handlers are supported and run in parallel per message.
    /// Must be called before <see cref="StartAsync"/>.
    /// </summary>
    void Register(Func<string, Task> handler);

    /// <summary>Starts consuming messages from the underlying transport.</summary>
    Task StartAsync(CancellationToken cancellationToken = default);
}
