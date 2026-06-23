using BuildingOs.ApiServer.Modules;
using BuildingOS.Shared.Domain.OidcClients;
using BuildingOS.Shared.Domain.UserManagement;
using Microsoft.AspNetCore.Authentication.JwtBearer;

namespace BuildingOs.ApiServer;

public static partial class IServiceCollectionExtension
{
    /// <summary>
    /// Registers JWT authentication via Keycloak OIDC discovery.
    /// </summary>
    public static IServiceCollection AddOidcAuthentication(
        this IServiceCollection services,
        EnvModule envModule)
    {
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.Authority = envModule.KeycloakAuthority;
                options.Audience = envModule.KeycloakClientId;
                options.RequireHttpsMetadata =
                    !string.IsNullOrEmpty(envModule.KeycloakAuthority) &&
                    envModule.KeycloakAuthority.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
                if (!string.IsNullOrEmpty(envModule.KeycloakValidIssuer))
                    options.TokenValidationParameters.ValidIssuer = envModule.KeycloakValidIssuer;
            });

        return services;
    }

    /// <summary>
    /// Registers IUserManagementService via Keycloak Admin REST API.
    /// </summary>
    public static IServiceCollection AddUserManagementService(
        this IServiceCollection services,
        EnvModule envModule)
    {
        if (!string.IsNullOrEmpty(envModule.KeycloakAuthority) &&
            !string.IsNullOrEmpty(envModule.KeycloakAdminClientId) &&
            !string.IsNullOrEmpty(envModule.KeycloakRealm))
        {
            services.AddHttpClient<IUserManagementService, KeycloakUserManagementService>(
                (sp, client) =>
                {
                    client.BaseAddress = new Uri(envModule.KeycloakAuthority!);
                })
                .AddTypedClient<IUserManagementService>((client, sp) =>
                    new KeycloakUserManagementService(
                        client,
                        envModule.KeycloakRealm!,
                        envModule.KeycloakAdminClientId!,
                        envModule.KeycloakAdminClientSecret ?? string.Empty,
                        sp.GetRequiredService<ILogger<KeycloakUserManagementService>>()));
        }

        return services;
    }

    /// <summary>
    /// Registers <see cref="IOidcClientManagementService"/> via the Keycloak admin REST API when the
    /// admin env is configured; otherwise registers <see cref="UnconfiguredOidcClientService"/> so the
    /// controller returns 503 instead of failing DI activation (#324).
    /// </summary>
    public static IServiceCollection AddOidcClientManagementService(
        this IServiceCollection services,
        EnvModule envModule)
    {
        if (!string.IsNullOrEmpty(envModule.KeycloakAuthority) &&
            !string.IsNullOrEmpty(envModule.KeycloakAdminClientId) &&
            !string.IsNullOrEmpty(envModule.KeycloakRealm))
        {
            services.AddHttpClient<IOidcClientManagementService, KeycloakOidcClientService>(
                (sp, client) =>
                {
                    client.BaseAddress = new Uri(envModule.KeycloakAuthority!);
                })
                .AddTypedClient<IOidcClientManagementService>((client, sp) =>
                    new KeycloakOidcClientService(
                        client,
                        envModule.KeycloakRealm!,
                        envModule.KeycloakAdminClientId!,
                        envModule.KeycloakAdminClientSecret ?? string.Empty,
                        sp.GetRequiredService<ILogger<KeycloakOidcClientService>>()));
        }
        else
        {
            services.AddSingleton<IOidcClientManagementService, UnconfiguredOidcClientService>();
        }

        return services;
    }
}
