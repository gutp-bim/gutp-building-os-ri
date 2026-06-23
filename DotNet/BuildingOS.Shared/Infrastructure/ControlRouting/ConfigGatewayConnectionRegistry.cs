namespace BuildingOS.Shared.Infrastructure.ControlRouting;

/// <summary>
/// Configuration-backed <see cref="IGatewayConnectionRegistry"/> (#154 Phase 2, plan §1 案A). Holds an
/// in-memory <c>gatewayId → bindingType</c> map plus, per gateway, an optional connection-settings
/// map. Settings resolution <em>merges</em> the layers (most specific wins):
/// <list type="number">
///   <item>the binding's default settings, synthesised by the DI layer from the existing
///   single-instance env (e.g. <c>HONO_AMQP_*</c>) — the base;</item>
///   <item>per-gateway settings (<c>Gateways:{id}:Settings:*</c>) overlaid on top, so a gateway that
///   overrides only <c>host</c> still inherits the binding's default tenant/port/credentials.</item>
/// </list>
/// The default binding exists so an unmapped gateway preserves the previous "always Hono" egress
/// behaviour (no regression); specific gateways are overridden via the map. Gateway-id and settings-key
/// lookups are case-insensitive so config-vs-twin casing differences do not silently drop overrides.
/// </summary>
public sealed class ConfigGatewayConnectionRegistry : IGatewayConnectionRegistry
{
    private static readonly IReadOnlyDictionary<string, string> Empty =
        new Dictionary<string, string>();

    private readonly IReadOnlyDictionary<string, string> _bindingByGateway;
    private readonly string _defaultBinding;
    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> _settingsByGateway;
    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> _defaultSettingsByBinding;

    public ConfigGatewayConnectionRegistry(
        IReadOnlyDictionary<string, string> bindingByGateway,
        string defaultBinding,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> settingsByGateway,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> defaultSettingsByBinding)
    {
        // Case-insensitive gateway-id and binding lookups (outer keys) and settings-key lookups (inner
        // keys): config-key casing need not match the twin's (gateway id), the binding constants, or
        // the settings keys the adapters read (e.g. "host"). Inner maps are normalised too so a caller
        // passing a plain dictionary still gets case-insensitive settings.
        _bindingByGateway = new Dictionary<string, string>(bindingByGateway, StringComparer.OrdinalIgnoreCase);
        _defaultBinding = defaultBinding;
        _settingsByGateway = NormaliseInner(settingsByGateway);
        _defaultSettingsByBinding = NormaliseInner(defaultSettingsByBinding);
    }

    public GatewayConnection? Resolve(string? gatewayId)
    {
        var binding = !string.IsNullOrEmpty(gatewayId)
            && _bindingByGateway.TryGetValue(gatewayId, out var mapped)
                ? mapped
                : _defaultBinding;

        if (string.IsNullOrEmpty(binding)) return null;

        // Normalise so config typos in casing (e.g. "Hono", "Bacnet-Sim") still resolve.
        binding = binding.ToLowerInvariant();

        var settings = MergeSettings(gatewayId, binding);
        return new GatewayConnection(gatewayId ?? string.Empty, binding, settings);
    }

    /// <summary>Binding defaults as the base, per-gateway settings overlaid on top (per-gateway wins).</summary>
    private IReadOnlyDictionary<string, string> MergeSettings(string? gatewayId, string binding)
    {
        var defaults = _defaultSettingsByBinding.TryGetValue(binding, out var byBinding) ? byBinding : Empty;
        var perGateway = !string.IsNullOrEmpty(gatewayId) && _settingsByGateway.TryGetValue(gatewayId, out var pg)
            ? pg
            : Empty;

        if (perGateway.Count == 0) return defaults;
        if (defaults.Count == 0) return perGateway;

        var merged = new Dictionary<string, string>(defaults, StringComparer.OrdinalIgnoreCase);
        foreach (var kv in perGateway) merged[kv.Key] = kv.Value;
        return merged;
    }

    private static IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> NormaliseInner(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> source)
        => source.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(kv.Value, StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
}
