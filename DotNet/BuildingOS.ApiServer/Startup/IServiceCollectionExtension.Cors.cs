namespace BuildingOs.ApiServer;

public static partial class IServiceCollectionExtension
{
    private const string MyAllowSpecificOrigins = "_myAllowSpecificOrigins";

    public static IServiceCollection AddCorsForAll(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var origins = (configuration["CORS_ALLOWED_ORIGINS"] ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return services.AddCors(o => o.AddPolicy(MyAllowSpecificOrigins, builder =>
        {
            if (origins.Length == 0)
            {
                // No origins configured: open for development. Set CORS_ALLOWED_ORIGINS in production.
                builder.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
            }
            else
            {
                builder.WithOrigins(origins).AllowAnyMethod().AllowAnyHeader();
            }
        }));
    }
}