using BuildingOS.Shared.Infrastructure.ControlRouting;

namespace BuildingOS.Shared.Test.Infrastructure.ControlRouting;

public class ConfigGatewayConnectionRegistryTest
{
    private static ConfigGatewayConnectionRegistry Build(
        Dictionary<string, string>? map = null,
        string defaultBinding = "hono",
        Dictionary<string, IReadOnlyDictionary<string, string>>? perGatewaySettings = null,
        Dictionary<string, IReadOnlyDictionary<string, string>>? defaultSettingsByBinding = null)
        => new(map ?? new(), defaultBinding,
            perGatewaySettings ?? new(), defaultSettingsByBinding ?? new());

    [Fact]
    public void Resolve_ReturnsMappedBinding_WhenGatewayIsMapped()
    {
        var registry = Build(new() { ["gw-sim-1"] = "bacnet-sim" });
        var conn = registry.Resolve("gw-sim-1");

        Assert.NotNull(conn);
        Assert.Equal("gw-sim-1", conn!.GatewayId);
        Assert.Equal("bacnet-sim", conn.BindingType);
    }

    [Fact]
    public void Resolve_FallsBackToDefaultBinding_WhenGatewayUnmapped()
    {
        // No-regression: an unmapped gateway keeps the previous "always Hono" behaviour.
        var registry = Build(new() { ["gw-sim-1"] = "bacnet-sim" });
        var conn = registry.Resolve("unknown-gw");

        Assert.Equal("hono", conn!.BindingType);
    }

    [Fact]
    public void Resolve_ReturnsDefaultBinding_WhenGatewayIdNullOrEmpty()
    {
        var registry = Build();
        Assert.Equal("hono", registry.Resolve(null)!.BindingType);
        Assert.Equal("hono", registry.Resolve("")!.BindingType);
    }

    [Fact]
    public void Resolve_UsesPerGatewaySettings_WhenPresent()
    {
        // Two same-binding gateways can point at different hosts (the Phase 2 goal).
        var registry = Build(
            map: new() { ["gw-a"] = "hono", ["gw-b"] = "hono" },
            perGatewaySettings: new()
            {
                ["gw-a"] = new Dictionary<string, string> { ["host"] = "hono-a" },
                ["gw-b"] = new Dictionary<string, string> { ["host"] = "hono-b" },
            });

        Assert.Equal("hono-a", registry.Resolve("gw-a")!.Get("host"));
        Assert.Equal("hono-b", registry.Resolve("gw-b")!.Get("host"));
    }

    [Fact]
    public void Resolve_FallsBackToDefaultSettingsForBinding_WhenNoPerGatewaySettings()
    {
        // Backward compat: an unmapped/under-specified gateway inherits the binding's default
        // settings (synthesised from the existing single-instance env by the DI layer).
        var registry = Build(
            defaultBinding: "hono",
            defaultSettingsByBinding: new()
            {
                ["hono"] = new Dictionary<string, string> { ["host"] = "env-hono", ["tenant"] = "building-os" },
            });

        var conn = registry.Resolve("any-gw");
        Assert.Equal("env-hono", conn!.Get("host"));
        Assert.Equal("building-os", conn.Get("tenant"));
    }

    [Fact]
    public void Resolve_PrefersPerGatewaySettings_OverBindingDefaults()
    {
        var registry = Build(
            map: new() { ["gw-a"] = "hono" },
            perGatewaySettings: new()
            {
                ["gw-a"] = new Dictionary<string, string> { ["host"] = "override-host" },
            },
            defaultSettingsByBinding: new()
            {
                ["hono"] = new Dictionary<string, string> { ["host"] = "env-hono" },
            });

        Assert.Equal("override-host", registry.Resolve("gw-a")!.Get("host"));
    }

    [Fact]
    public void Resolve_MergesPerGatewayOntoBindingDefaults_PartialOverrideInheritsRest()
    {
        // A gateway overriding only `host` still inherits the binding's default tenant/port/credentials
        // (merge, not replace) — the key backward-compat guarantee for partial per-gateway config.
        var registry = Build(
            map: new() { ["gw-a"] = "hono" },
            perGatewaySettings: new()
            {
                ["gw-a"] = new Dictionary<string, string> { ["host"] = "hono-a" },
            },
            defaultSettingsByBinding: new()
            {
                ["hono"] = new Dictionary<string, string> { ["host"] = "env-hono", ["tenant"] = "building-os", ["port"] = "5671" },
            });

        var conn = registry.Resolve("gw-a");
        Assert.Equal("hono-a", conn!.Get("host"));      // per-gateway override
        Assert.Equal("building-os", conn.Get("tenant")); // inherited from binding default
        Assert.Equal("5671", conn.Get("port"));          // inherited from binding default
    }

    [Fact]
    public void Resolve_LookupIsCaseInsensitive_ForGatewayIdAndSettingsKey()
    {
        var registry = Build(
            map: new() { ["Gw-A"] = "hono" },
            perGatewaySettings: new()
            {
                ["Gw-A"] = new Dictionary<string, string> { ["Host"] = "hono-a" },
            });

        var conn = registry.Resolve("gw-a"); // different casing than the config key
        Assert.Equal("hono", conn!.BindingType);
        Assert.Equal("hono-a", conn.Get("host")); // settings key casing differs too
    }

    [Fact]
    public void Resolve_HonoursConfiguredDefaultBinding()
    {
        var registry = Build(defaultBinding: "bacnet-sim");
        Assert.Equal("bacnet-sim", registry.Resolve("anything")!.BindingType);
    }

    [Fact]
    public void Resolve_ReturnsEmptySettings_WhenNoneConfigured()
    {
        var conn = Build().Resolve("gw");
        Assert.NotNull(conn);
        Assert.Null(conn!.Get("host"));
    }

    [Fact]
    public void Get_ReturnsFallback_WhenKeyMissingOrEmpty()
    {
        var conn = new GatewayConnection("gw", "hono",
            new Dictionary<string, string> { ["host"] = "", ["tenant"] = "t" });
        Assert.Equal("default", conn.Get("host", "default"));
        Assert.Equal("default", conn.Get("missing", "default"));
        Assert.Equal("t", conn.Get("tenant", "default"));
    }
}
