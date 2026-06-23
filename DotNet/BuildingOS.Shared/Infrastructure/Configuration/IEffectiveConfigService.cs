using BuildingOS.Shared.Domain.Configuration;

namespace BuildingOS.Shared.Infrastructure.Configuration;

/// <summary>
/// Exposes the API server's effective configuration (allowlisted, secrets masked) for the read-only
/// config view (#147). IaC/ArgoCD remains the source of truth; this is observability only.
/// </summary>
public interface IEffectiveConfigService
{
    EffectiveConfig GetEffectiveConfig();
}
