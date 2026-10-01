using BuildingOS.Shared.Domain.Authorization;
using BuildingOS.Shared.Infrastructure.Authorization;
using BuildingOs.ApiServer.Authorization;
using BuildingOs.ApiServer.GatewayProvisioning;

namespace BuildingOs.ApiServer;

public static partial class IServiceCollectionExtension
{
    public static IServiceCollection AddAuth(this IServiceCollection services)
    {
        services.AddSingleton<IResourceHierarchyResolver, OxiGraphHierarchyResolver>();
        // Inverse of the hierarchy resolver, same paths (#509): my-resources?expand=descendants.
        services.AddSingleton<IResourceDescendantResolver, OxiGraphDescendantResolver>();
        services.AddScoped<BuildingOS.Shared.Domain.Authorization.IAuthorizationService, DefaultAuthorizationService>();
        // Both depend on RelationalDbContext/IGroupRepository registered unconditionally in Startup.ConfigureServices.
        services.AddScoped<IGroupMembershipResolver, GroupMembershipResolver>();
        services.AddScoped<IResourceIdMappingRepository, ResourceIdMappingRepository>();
        // Singleton: it owns a cross-request cache and resolves in a DI scope of its own (#548).
        services.AddSingleton<INavigableAncestorResolver, NavigableAncestorResolver>();
        services.AddScoped<IAuthorizedTwinView, AuthorizedTwinView>();
        // Gateway provisioning (#224): identity from the mTLS-derived trusted header (ingress-injected).
        services.AddSingleton<IGatewayIdentityResolver>(_ => new HeaderGatewayIdentityResolver());
        // Point-list snapshot store for ?since= diffs (#224/diff). IMemoryCache is registered in Startup.
        services.AddSingleton<IGatewayPointListSnapshotStore, MemoryGatewayPointListSnapshotStore>();
        services.AddSingleton<IPointListRevisionCoordinator, NatsKvPointListRevisionCoordinator>();
        return services;
    }
}
