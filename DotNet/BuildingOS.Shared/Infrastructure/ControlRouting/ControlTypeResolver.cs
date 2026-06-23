using System.Text.Json;
using BuildingOS.Shared.Domain;

namespace BuildingOS.Shared.Infrastructure.ControlRouting;

/// <summary>
/// Default <see cref="IControlTypeResolver"/>: maps a gateway's binding type (resolved via
/// <see cref="IGatewayConnectionRegistry"/>) to an egress ControlType and builds the type-specific
/// Body. New protocols are added by extending the binding-type switch with a Body builder (plan §1).
/// </summary>
public sealed class ControlTypeResolver : IControlTypeResolver
{
    private readonly IGatewayConnectionRegistry _connections;

    public ControlTypeResolver(IGatewayConnectionRegistry connections)
        => _connections = connections;

    public ControlDispatch? Resolve(Point point, Device? device, double value)
    {
        // Mirror the writable gate (#139): only an explicit false blocks control; null is permitted.
        if (point.Writable == false) return null;

        var gatewayId = device?.GatewayId;
        // The registry resolves the gateway's binding type (config-driven, already lower-cased).
        var binding = _connections.Resolve(gatewayId)?.BindingType;

        return binding switch
        {
            BindingTypes.Hono      => new ControlDispatch(DeviceControlType.Hono, BuildHonoBody(value), gatewayId),
            BindingTypes.BacnetSim => BuildBacnetSimDispatch(value, gatewayId),
            _                      => null, // unsupported binding (e.g. kandt body building is not API-wired yet)
        };
    }

    private static string BuildHonoBody(double value)
        => JsonSerializer.Serialize(new { value });

    /// <summary>
    /// Builds the point-id-canonical BacnetSim command body (#181). BuildingOS and the gateway share
    /// the point list, so the gateway resolves <c>point_id</c> → BACnet object/instance locally; the
    /// command body therefore carries only the value (point_id rides on the PointControlInfo). Returns
    /// null when no gatewayId is set, since BacnetSim is delivered via the per-gateway bridge subject.
    /// </summary>
    private static ControlDispatch? BuildBacnetSimDispatch(double value, string? gatewayId)
    {
        // BacnetSim is delivered via the gateway bridge's per-gateway subject, so a gatewayId is
        // mandatory — without it the command cannot be routed to a bridge replica.
        if (string.IsNullOrEmpty(gatewayId)) return null;

        var body = JsonSerializer.Serialize(new { value });
        return new ControlDispatch(DeviceControlType.BacnetSim, body, gatewayId);
    }
}
