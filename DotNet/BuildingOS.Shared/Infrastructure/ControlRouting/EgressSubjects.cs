namespace BuildingOS.Shared.Infrastructure.ControlRouting;

/// <summary>
/// NATS subject conventions for control egress (plan §3-3). Per-gateway request subjects let a
/// command reach whichever GatewayBridge replica currently holds that gateway's stream: each
/// replica subscribes only to the gateways it is connected to, so the bridge stays stateless and
/// the LB can route the gRPC connection to any pod.
/// </summary>
public static class EgressSubjects
{
    /// <summary>Generic control request consumed by in-process handlers (Hono/Kandt) in ConnectorWorker.</summary>
    public const string GenericRequest = "building-os.control.request";

    private const string PerGatewayRequestPrefix = "building-os.control.request.gw.";
    private const string ResultPrefix = "building-os.control.result.";
    private const string PointListUpdatePrefix = "building-os.pointlist.updated.gw.";

    /// <summary>Per-gateway request subject: <c>building-os.control.request.gw.{gatewayId}</c>.</summary>
    public static string PerGatewayRequest(string gatewayId) => PerGatewayRequestPrefix + gatewayId;

    /// <summary>Result subject consumed by the existing result bus / WaitForResult.</summary>
    public static string Result(string controlId) => ResultPrefix + controlId;

    /// <summary>
    /// Per-gateway point-list update subject: <c>building-os.pointlist.updated.gw.{gatewayId}</c>
    /// (#224/push). The bridge replica holding the gateway's egress stream subscribes and forwards a
    /// PointListUpdate down the stream.
    /// </summary>
    public static string PointListUpdate(string gatewayId) => PointListUpdatePrefix + gatewayId;
}
