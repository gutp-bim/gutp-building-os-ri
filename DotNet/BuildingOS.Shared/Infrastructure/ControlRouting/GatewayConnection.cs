namespace BuildingOS.Shared.Infrastructure.ControlRouting;

/// <summary>
/// A gateway instance's resolved egress connection (#154 Phase 2): its <see cref="BindingType"/>
/// (the protocol the adapter speaks) plus the per-instance connection <see cref="Settings"/>
/// (host/port/tenant/credentials, etc.). Resolved by <see cref="IGatewayConnectionRegistry"/> from
/// the config layer so adapters no longer read process-wide env directly — two gateways of the same
/// binding can therefore point at different hosts.
/// </summary>
/// <param name="GatewayId">The gateway this connection belongs to (empty when resolved as the default).</param>
/// <param name="BindingType">The binding/protocol key (<see cref="BindingTypes"/>): hono / kandt / bacnet-sim.</param>
/// <param name="Settings">
/// Connection parameters keyed by adapter-specific names (e.g. host, port, tenant). Secrets are read
/// from config/env into this map by the registry; a <c>credentialsRef</c> entry is reserved for a
/// future external secret-store resolver (out of scope for this slice).
/// </param>
public sealed record GatewayConnection(
    string GatewayId,
    string BindingType,
    IReadOnlyDictionary<string, string> Settings)
{
    /// <summary>Returns the setting for <paramref name="key"/>, or <paramref name="fallback"/> when absent/empty.</summary>
    public string? Get(string key, string? fallback = null)
        => Settings.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? v : fallback;
}

/// <summary>Well-known binding-type keys (lower-case; the config-driven connection vocabulary).</summary>
public static class BindingTypes
{
    public const string Hono = "hono";
    public const string Kandt = "kandt";
    public const string BacnetSim = "bacnet-sim";

    /// <summary>OSS/CI simulated handler (no real gateway); reachable only when a gateway is mapped to it.</summary>
    public const string Simulated = "simulated";
}
