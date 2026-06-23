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
        services.AddScoped<BuildingOS.Shared.Domain.Authorization.IAuthorizationService, DefaultAuthorizationService>();
        // Both depend on RelationalDbContext/IGroupRepository registered unconditionally in Startup.ConfigureServices.
        services.AddScoped<IGroupMembershipResolver, GroupMembershipResolver>();
        services.AddScoped<IResourceIdMappingRepository, ResourceIdMappingRepository>();
        services.AddScoped<IAuthorizedTwinView, AuthorizedTwinView>();
        // Gateway provisioning (#224): identity from the mTLS-derived trusted header (ingress-injected).
        services.AddSingleton<IGatewayIdentityResolver>(_ => new HeaderGatewayIdentityResolver());
        // Point-list snapshot store for ?since= diffs (#224/diff). IMemoryCache is registered in Startup.
        services.AddSingleton<IGatewayPointListSnapshotStore, MemoryGatewayPointListSnapshotStore>();
        return services;
    }
}