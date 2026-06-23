namespace BuildingOS.Shared.Infrastructure.ControlRouting;

/// <summary>
/// Resolves a gateway's egress <see cref="GatewayConnection"/> (#154 Phase 2): its binding type and
/// the per-instance connection settings. This is the dedicated config layer (plan §1) — the binding
/// is driven by configuration keyed on the gatewayId, never inferred from an id prefix. It supersedes
/// the previous <c>IGatewayConnectionTypeProvider</c>, which returned only the connection-type string.
/// </summary>
public interface IGatewayConnectionRegistry
{
    /// <summary>
    /// Returns the connection for <paramref name="gatewayId"/>, falling back to the configured default
    /// binding (and its default settings) when the gateway is not explicitly mapped or the id is
    /// null/empty. Returns <c>null</c> only when no default binding is configured.
    /// </summary>
    GatewayConnection? Resolve(string? gatewayId);
}
