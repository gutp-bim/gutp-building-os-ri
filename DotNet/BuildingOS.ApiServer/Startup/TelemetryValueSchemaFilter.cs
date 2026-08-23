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

        var branches = BranchesFor(context);
        if (branches is null) return;

        // Exactly one branch carries null. Putting `nullable: true` on the parent is invalid in 3.0
        // without a sibling `type` (redocly flags nullable-type-sibling), and marking every branch
        // nullable would make `null` match all of them — `oneOf` means *exactly one*, so a strict
        // validator would then reject the `null` this API genuinely returns for a point with no
        // reading. One nullable branch is both spec-clean and semantically exact.
        //
        // Never express this as a multi-flag JsonSchemaType (Number|String|Boolean): the 3.0
        // serializer silently degrades that to `{"nullable": true}`, losing every type, while all
        // in-memory assertions still pass.
        target.Type = null;
        target.Format = null;
        target.OneOf = branches;
    }

    /// <summary>
    /// The <c>oneOf</c> branches for a telemetry wire DTO's union-typed property, or <c>null</c> if
    /// the property is not one of them.
    /// <para>
    /// Deliberately keyed on the declaring type and property name rather than
    /// <c>context.Type == typeof(object)</c>: the latter would rewrite every <c>object?</c> property
    /// in the document — untestable today, and it would silently start mangling the first unrelated
    /// one someone adds.
    /// </para>
    /// <para>
    /// The two properties do <b>not</b> share a branch set. <c>value</c> is the reading itself and
    /// can be a number; <c>state</c> is only ever the non-numeric half (#359), so admitting a number
    /// there would advertise a shape the server never produces.
    /// </para>
    /// </summary>
    private static List<IOpenApiSchema>? BranchesFor(SchemaFilterContext context)
    {
        if (context.MemberInfo is not PropertyInfo property) return null;
        if (property.DeclaringType != typeof(TelemetryReading)
            && property.DeclaringType != typeof(LatestSample)) return null;

        return property.Name switch
        {
            nameof(TelemetryReading.Value) =>
            [
                new OpenApiSchema { Type = JsonSchemaType.Number | JsonSchemaType.Null, Format = "double" },
                new OpenApiSchema { Type = JsonSchemaType.String },
                new OpenApiSchema { Type = JsonSchemaType.Boolean },
            ],
            nameof(TelemetryReading.State) =>
            [
                new OpenApiSchema { Type = JsonSchemaType.String | JsonSchemaType.Null },
                new OpenApiSchema { Type = JsonSchemaType.Boolean },
            ],
            _ => null,
        };
    }
}
