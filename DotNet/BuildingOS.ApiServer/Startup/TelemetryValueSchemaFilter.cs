using BuildingOs.ApiServer.Controllers;
using BuildingOs.ApiServer.Telemetry;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;
using System.Reflection;

namespace BuildingOs.ApiServer;

/// <summary>
/// Widens the telemetry <c>value</c> property to <c>oneOf: [number, string, boolean]</c> in the
/// generated OpenAPI document (#344).
///
/// <para>
/// The C# property is <c>object?</c> so System.Text.Json writes the reading by its runtime type.
/// Swashbuckle maps <c>object</c> to a schema with no <c>type</c>, and <c>openapi2aspida</c> drops a
/// typeless property from the generated TypeScript entirely — so without this filter the field the
/// change exists to expose would vanish from every generated client. The filter is load-bearing, not
/// cosmetic.
/// </para>
///
/// <para>
/// Registered <b>after</b> <c>IncludeXmlComments</c> in
/// <c>IServiceCollectionExtension.Swagger.cs</c>: filters run in registration order, and Swashbuckle's
/// own XML-comment filter would otherwise overwrite the description afterwards.
/// </para>
/// </summary>
public sealed class TelemetryValueSchemaFilter : ISchemaFilter
{
    public void Apply(IOpenApiSchema schema, SchemaFilterContext context)
    {
        // Swashbuckle 10 hands filters an IOpenApiSchema, and for a $ref'd component it passes an
        // OpenApiSchemaReference, which is not the mutable document node. Casting blindly throws at
        // swagger-generation time — i.e. it breaks Tools/sync-type.bash and the #354 CI step, not a
        // unit test — so fail soft.
        if (schema is not OpenApiSchema target) return;
        if (!IsTelemetryValueProperty(context)) return;

        // Exactly one branch carries null. Putting `nullable: true` on the parent is invalid in 3.0
        // without a sibling `type` (redocly flags nullable-type-sibling), and marking all three
        // nullable would make `null` match every branch — `oneOf` means *exactly one*, so a strict
        // validator would then reject `"value": null`, which this API genuinely returns for a point
        // with no reading. One nullable branch is both spec-clean and semantically exact.
        //
        // Never express this as a multi-flag JsonSchemaType (Number|String|Boolean): the 3.0
        // serializer silently degrades that to `{"nullable": true}`, losing every type, while all
        // in-memory assertions still pass.
        target.Type = null;
        target.Format = null;
        target.OneOf =
        [
            new OpenApiSchema { Type = JsonSchemaType.Number | JsonSchemaType.Null, Format = "double" },
            new OpenApiSchema { Type = JsonSchemaType.String },
            new OpenApiSchema { Type = JsonSchemaType.Boolean },
        ];
    }

    /// <summary>
    /// Matches only the two telemetry wire DTOs' <c>Value</c> property.
    /// <para>
    /// Deliberately not <c>context.Type == typeof(object)</c>: that would rewrite every
    /// <c>object?</c> property in the document. There are none today, so such a filter would be
    /// untestable and would silently start mangling the first one someone adds.
    /// </para>
    /// </summary>
    private static bool IsTelemetryValueProperty(SchemaFilterContext context) =>
        context.MemberInfo is PropertyInfo property
        && property.Name == nameof(TelemetryReading.Value)
        && (property.DeclaringType == typeof(TelemetryReading)
            || property.DeclaringType == typeof(LatestSample));
}
