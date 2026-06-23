using BuildingOS.Shared.Domain.Configuration;
using Microsoft.Extensions.Configuration;

namespace BuildingOS.Shared.Infrastructure.Configuration;

/// <summary>
/// Default <see cref="IEffectiveConfigService"/>: reads the allowlisted keys from
/// <see cref="IConfiguration"/> and masks secrets via <see cref="EffectiveConfigBuilder"/>. The
/// display key uses the env-var <c>__</c> form; lookup translates it to the <c>:</c> hierarchy that
/// <see cref="IConfiguration"/> uses (e.g. <c>Logging__LogLevel__Default</c> → <c>Logging:LogLevel:Default</c>).
/// </summary>
public sealed class EffectiveConfigService : IEffectiveConfigService
{
    private readonly IConfiguration _config;

    public EffectiveConfigService(IConfiguration config)
    {
        _config = config;
    }

    public EffectiveConfig GetEffectiveConfig() =>
        EffectiveConfigBuilder.Build(
            ConfigAllowlist.ApiServer,
            key => _config[key.Replace("__", ":")]);
}
