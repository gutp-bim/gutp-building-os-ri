using BuildingOS.Shared.Domain;

namespace BuildingOS.Shared.Infrastructure.ControlRouting;

/// <summary>
/// Resolves a point + value into a concrete egress <see cref="ControlDispatch"/> (ControlType +
/// serialized Body + gatewayId), replacing the previous hard-coded <c>ControlType=Hono</c> in
/// <c>PointController</c>. The binding type is looked up via
/// <see cref="IGatewayConnectionRegistry"/> (dedicated config), never inferred from id prefixes.
/// </summary>
public interface IControlTypeResolver
{
    /// <summary>
    /// Returns the dispatch for the command, or <c>null</c> when the point cannot be controlled
    /// (not writable, or the gateway's binding type is unsupported / not API-wired).
    /// </summary>
    ControlDispatch? Resolve(Point point, Device? device, double value);
}

/// <summary>Resolved egress command: which handler (<see cref="ControlType"/>), the serialized
/// <see cref="Body"/>, and the target <see cref="GatewayId"/> (may be null).</summary>
public record ControlDispatch(string ControlType, string Body, string? GatewayId);
