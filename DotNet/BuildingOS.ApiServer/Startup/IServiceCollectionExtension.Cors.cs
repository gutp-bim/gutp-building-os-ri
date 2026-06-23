namespace BuildingOs.ApiServer;

public static partial class IServiceCollectionExtension
{
    private const string MyAllowSpecificOrigins = "_myAllowSpecificOrigins";

    public static IServiceCollection AddCorsForAll(this IServiceCollection services) =>
        services.AddCors(o => o.AddPolicy(MyAllowSpecificOrigins, builder =>
        {
            builder.AllowAnyOrigin()
                .AllowAnyMethod()
                .AllowAnyHeader();
        }));
}