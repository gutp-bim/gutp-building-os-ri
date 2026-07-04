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
        var isDevelopment = string.Equals(
            configuration["ASPNETCORE_ENVIRONMENT"], "Development",
            StringComparison.OrdinalIgnoreCase);

        return services.AddCors(o => o.AddPolicy(MyAllowSpecificOrigins, builder =>
        {
            if (origins.Length > 0)
            {
                builder.WithOrigins(origins).AllowAnyMethod().AllowAnyHeader();
            }
            else if (isDevelopment)
            {
                // No origins configured in Development: open for local dev.
                // In all other environments this falls through to fail-closed (no origins permitted).
                builder.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader();
            }
            // else: fail-closed — no CORS headers emitted; cross-origin requests are denied.
        }));
    }
}