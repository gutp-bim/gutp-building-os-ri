using Microsoft.Extensions.Configuration;

namespace BuildingOS.Shared.Infrastructure.ControlRouting;

/// <summary>
/// Builds the <see cref="ConfigGatewayConnectionRegistry"/> from <see cref="IConfiguration"/> so both
/// the ApiServer and the ConnectorWorker construct an identical registry (#154 Phase 2). Centralising
/// the env reads here is what lets the protocol adapters stop reading env directly.
///
/// Config surface:
/// <list type="bullet">
///   <item><c>GatewayConnectionTypes:Default</c> — default binding for unmapped gateways (kept for
///   backward compat; defaults to <c>hono</c>).</item>
///   <item><c>GatewayConnectionTypes:Map:{gatewayId}</c> — per-gateway binding override.</item>
///   <item><c>Gateways:{gatewayId}:Settings:{key}</c> — per-gateway connection settings (host/port/…).</item>
/// </list>
/// A gateway's settings are the binding's <em>default</em> settings (synthesised from the existing
/// single-instance env — <c>HONO_AMQP_*</c>, <c>IOT_HUB_*</c>) with its per-gateway settings overlaid
/// on top, so existing single-gateway deployments keep working and a partial per-gateway override
/// inherits the rest.
/// </summary>
public static class GatewayConnectionRegistryFactory
{
    /// <param name="fallbackDefaultBinding">
    /// Default binding to use when <c>GatewayConnectionTypes:Default</c> is not set. Lets a host align
    /// the default with the handlers it actually registered (e.g. the ConnectorWorker passes
    /// <c>simulated</c> in sim mode so an unmapped gateway dispatches to the only registered handler
    /// instead of resolving to <c>hono</c> with no Hono handler present). Defaults to <c>hono</c>.
    /// </param>
    public static ConfigGatewayConnectionRegistry Create(IConfiguration config, string? fallbackDefaultBinding = null)
    {
        var bindingByGateway = SectionToDictionary(config.GetSection("GatewayConnectionTypes:Map"));

        var defaultBinding = config["GatewayConnectionTypes:Default"]
            ?? fallbackDefaultBinding ?? BindingTypes.Hono;

        var settingsByGateway = new Dictionary<string, IReadOnlyDictionary<string, string>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var gw in config.GetSection("Gateways").GetChildren())
        {
            var settings = SectionToDictionary(gw.GetSection("Settings"));
            if (settings.Count > 0)
                settingsByGateway[gw.Key] = settings;
        }

        var defaultSettingsByBinding = new Dictionary<string, IReadOnlyDictionary<string, string>>(
            StringComparer.OrdinalIgnoreCase)
        {
            [BindingTypes.Hono] = NonEmpty(new Dictionary<string, string?>
            {
                ["host"] = config["HONO_AMQP_HOST"],
                ["port"] = config["HONO_AMQP_PORT"],
                ["user"] = config["HONO_AMQP_USER"],
                ["password"] = config["HONO_AMQP_PASSWORD"],
                ["tenant"] = config["HONO_AMQP_TENANT"],
                ["tls"] = config["HONO_AMQP_TLS"],
            }),
            [BindingTypes.Kandt] = NonEmpty(new Dictionary<string, string?>
            {
                ["iotHubConnectionString"] = config["IOT_HUB_CONNECTION_STRING"],
                ["moduleId"] = config["IOT_EDGE_MODULE_ID"],
            }),
        };

        return new ConfigGatewayConnectionRegistry(
            bindingByGateway, defaultBinding, settingsByGateway, defaultSettingsByBinding);
    }

    private static Dictionary<string, string> SectionToDictionary(IConfigurationSection section)
        => section.GetChildren()
            .Where(c => !string.IsNullOrEmpty(c.Value))
            .ToDictionary(c => c.Key, c => c.Value!, StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, string> NonEmpty(IDictionary<string, string?> source)
        => source.Where(kv => !string.IsNullOrEmpty(kv.Value))
                 .ToDictionary(kv => kv.Key, kv => kv.Value!, StringComparer.OrdinalIgnoreCase);
}
