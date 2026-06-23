namespace BuildingOS.GatewayBridge.Infrastructure;

/// <summary>
/// Transport for egress: per-gateway command delivery and result publishing. Abstracted so the
/// gRPC service can be tested without a live NATS server.
/// </summary>
public interface IEgressCommandBus
{
    /// <summary>
    /// Subscribes to the per-gateway request subject and invokes <paramref name="onCommand"/> with
    /// each raw command payload (PointControlInfo JSON). Dispose the result to unsubscribe.
    /// </summary>
    Task<IAsyncDisposable> SubscribeAsync(string gatewayId, Func<string, Task> onCommand, CancellationToken cancellationToken);

    /// <summary>Publishes a control result to the result subject consumed by the existing result bus.</summary>
    Task PublishResultAsync(string controlId, string resultJson, CancellationToken cancellationToken);

    /// <summary>
    /// Subscribes to the per-gateway point-list update subject and invokes <paramref name="onUpdate"/>
    /// with the raw payload (the revision string, possibly empty) for each signal (#224/push). Dispose
    /// to unsubscribe.
    /// </summary>
    Task<IAsyncDisposable> SubscribePointListUpdatesAsync(string gatewayId, Func<string, Task> onUpdate, CancellationToken cancellationToken);
}
