using BuildingOS.Shared.Domain;

namespace BuildingOS.Shared.Infrastructure.ControlRouting;

/// <summary>
/// Decides which NATS subject a control command is published to. Gateway-bridge-delivered control
/// types (BacnetSim) go to their per-gateway subject so they reach the replica holding that
/// gateway's stream; in-process handler types (Hono/Kandt) go to the generic request subject
/// consumed by ConnectorWorker.
/// </summary>
public static class ControlRequestRouting
{
    public static string SubjectFor(string controlType, string? gatewayId)
        => IsPerGatewayEgress(controlType, gatewayId)
            ? EgressSubjects.PerGatewayRequest(gatewayId!)
            : EgressSubjects.GenericRequest;

    /// <summary>
    /// True when the command goes to a per-gateway egress subject (delivered only to the live
    /// GatewayBridge replica holding that gateway) rather than the durable generic request stream.
    /// Only this path can silently drop on an offline gateway, so only it needs the liveness probe (#186).
    /// </summary>
    public static bool IsPerGatewayEgress(string controlType, string? gatewayId)
        => controlType == DeviceControlType.BacnetSim && !string.IsNullOrEmpty(gatewayId);
}
