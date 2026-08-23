using System.Reflection;
using Microsoft.OpenApi;

namespace BuildingOs.ApiServer;

public static partial class IServiceCollectionExtension
{
    public static IServiceCollection AddSwagger(this IServiceCollection self)
    {
        self.AddSwaggerGen(options =>
        {
            options.SupportNonNullableReferenceTypes();
            options.CustomSchemaIds(SwaggerSchemaId.For);
            options.SwaggerDoc("building-os", new OpenApiInfo
            {
                Title = "Building OS API",
                Version = "v1",
                Description = "An ASP.NET Core API for Building OS",
            });
            var xmlFilename = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
            options.IncludeXmlComments(Path.Combine(AppContext.BaseDirectory, xmlFilename));

            // Must come AFTER IncludeXmlComments: schema filters run in registration order, and
            // Swashbuckle's own XML-comment filter would otherwise run last and clobber what this
            // one writes. It widens the telemetry `value` to oneOf: [number, string, boolean] (#344)
            // — without it Swashbuckle emits a typeless schema for the `object?` property and
            // openapi2aspida drops the field from the generated client entirely.
            options.SchemaFilter<TelemetryValueSchemaFilter>();
        });
        return self;
    }
}
public static class SwaggerSchemaId
{
    // Distinct controllers can each define a same-named nested DTO (e.g. two
    // "SetEnabledRequest" records) without colliding on Swashbuckle's default
    // schemaId, which is just the bare type name.
    public static string For(Type type) =>
        type.IsNested && type.DeclaringType is not null
            ? $"{type.DeclaringType.Name}{type.Name}"
            : type.Name;
}
