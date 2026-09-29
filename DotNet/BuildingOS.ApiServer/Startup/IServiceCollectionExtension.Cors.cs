using BuildingOs.ApiServer.Telemetry;

namespace BuildingOs.ApiServer;

public static partial class IServiceCollectionExtension
{
    internal const string MyAllowSpecificOrigins = "_myAllowSpecificOrigins";

    /// <summary>
    /// Response headers cross-origin JS may read. Browsers hide anything outside the CORS safelist
    /// unless it is exposed, so a marker such as the partial-result pair (#499) would otherwise be
    /// invisible to a browser client on another origin.
    /// </summary>
    private static readonly string[] ExposedHeaders =
    {
        TelemetryResponseHeaders.PartialResult,
        TelemetryResponseHeaders.CoveredFrom,
        Controllers.PointController.ControlAuditNextCursorHeader,
        // A legacy path's deprecation notice (#507, ADR-0008).
        "Deprecation",
        "Link",
    };

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
                builder.WithOrigins(origins).AllowAnyMethod().AllowAnyHeader().WithExposedHeaders(ExposedHeaders);
            }
            else if (isDevelopment)
            {
                // No origins configured in Development: open for local dev.
                // In all other environments this falls through to fail-closed (no origins permitted).
                builder.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader().WithExposedHeaders(ExposedHeaders);
            }
            // else: fail-closed — no CORS headers emitted; cross-origin requests are denied.
        }));
    }
}