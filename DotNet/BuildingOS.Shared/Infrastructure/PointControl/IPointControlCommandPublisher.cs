using BuildingOS.Shared.Domain;

namespace BuildingOS.Shared.Infrastructure.PointControl;

/// <summary>
/// Abstracts how point control commands are dispatched — CosmosDB write (Azure) or NATS publish (OSS).
/// </summary>
public interface IPointControlCommandPublisher
{
    /// <summary>
    /// Dispatches the command and reports whether it reached a delivery surface. For per-gateway egress
    /// (BacnetSim via GatewayBridge) this detects an offline gateway at publish time (#186) so the
    /// caller can fail fast instead of waiting for the result timeout; in-process / durable paths always
    /// report <see cref="ControlDeliveryStatus.Delivered"/>.
    /// </summary>
    Task<ControlDeliveryStatus> PublishAsync(PointControlInfo command, CancellationToken cancellationToken = default);
}

/// <summary>Outcome of dispatching a control command to its transport.</summary>
public enum ControlDeliveryStatus
{
    /// <summary>Handed off to a delivery surface (durable stream, or a live gateway-bridge replica).</summary>
    Delivered,

    /// <summary>
    /// No gateway-bridge replica currently holds the target gateway's egress stream (NATS no-responders).
    /// The command was not delivered; the caller should fail fast (#186).
    /// </summary>
    GatewayOffline,
}
