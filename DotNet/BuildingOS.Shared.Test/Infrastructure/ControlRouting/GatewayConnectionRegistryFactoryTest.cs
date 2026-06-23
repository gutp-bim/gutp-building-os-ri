using BuildingOS.Shared.Infrastructure.ControlRouting;
using Microsoft.Extensions.Configuration;

namespace BuildingOS.Shared.Test.Infrastructure.ControlRouting;

public class GatewayConnectionRegistryFactoryTest
{
    private static IConfiguration Config(params (string Key, string Value)[] entries)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(entries.Select(e =>
                new KeyValuePair<string, string?>(e.Key, e.Value)))
            .Build();

    [Fact]
    public void Create_DefaultsToHono_WhenNoConfigAndNoFallback()
    {
        var registry = GatewayConnectionRegistryFactory.Create(Config());
        Assert.Equal("hono", registry.Resolve("any-gw")!.BindingType);
    }

    [Fact]
    public void Create_UsesFallbackDefault_WhenConfiguredDefaultUnset()
    {
        // sim mode: ConnectorWorker passes "simulated" so an unmapped gateway dispatches to the only
        // registered handler instead of resolving to hono with no Hono handler (Copilot #190).
        var registry = GatewayConnectionRegistryFactory.Create(Config(), fallbackDefaultBinding: "simulated");
        Assert.Equal("simulated", registry.Resolve("any-gw")!.BindingType);
    }

    [Fact]
    public void Create_PrefersExplicitDefault_OverFallback()
    {
        var registry = GatewayConnectionRegistryFactory.Create(
            Config(("GatewayConnectionTypes:Default", "hono")), fallbackDefaultBinding: "simulated");
        Assert.Equal("hono", registry.Resolve("any-gw")!.BindingType);
    }

    [Fact]
    public void Create_ReadsMapAndPerGatewaySettings()
    {
        var registry = GatewayConnectionRegistryFactory.Create(Config(
            ("GatewayConnectionTypes:Map:gw-a", "bacnet-sim"),
            ("Gateways:gw-h:Settings:host", "hono-h")));

        Assert.Equal("bacnet-sim", registry.Resolve("gw-a")!.BindingType);
        Assert.Equal("hono-h", registry.Resolve("gw-h")!.Get("host"));
    }
}
