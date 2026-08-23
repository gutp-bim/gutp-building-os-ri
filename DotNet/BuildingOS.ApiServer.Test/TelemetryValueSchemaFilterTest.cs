using BuildingOs.ApiServer;
using BuildingOs.ApiServer.Controllers;
using BuildingOs.ApiServer.Telemetry;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.SwaggerGen;
using System.Reflection;

namespace BuildingOS.ApiServer.Test;

/// <summary>
/// Tests for the filter that widens the telemetry <c>value</c> to <c>oneOf</c> (#344).
///
/// <para>
/// These assert on the <b>serialized</b> OpenAPI 3.0 output, not just the in-memory schema. That is
/// deliberate: expressing the union as a multi-flag <c>JsonSchemaType</c> leaves
/// <c>OneOf.Count == 3</c>-style assertions passing while the 3.0 serializer silently degrades the
/// document to <c>{"nullable": true}</c> with no type information at all — exactly the failure the
/// filter exists to prevent, and invisible to an in-memory check.
/// </para>
/// </summary>
public class TelemetryValueSchemaFilterTest
{
    private static string SerializeSchemaFor(Type dtoType)
    {
        var generator = new SchemaGenerator(
            new SchemaGeneratorOptions { SchemaFilters = { new TelemetryValueSchemaFilter() } },
            new Swashbuckle.AspNetCore.SwaggerGen.JsonSerializerDataContractResolver(
                new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web)));

        var repository = new SchemaRepository();
        generator.GenerateSchema(dtoType, repository);

        var schema = repository.Schemas[dtoType.Name];
        using var writer = new StringWriter();
        schema.SerializeAsV3(new OpenApiJsonWriter(writer));
        return writer.ToString();
    }

    /// <summary>
    /// Asserts on the serialized <c>oneOf</c> array specifically. A plain
    /// <c>Assert.Contains("\"string\"", json)</c> would be vacuous — the same DTO has several
    /// <c>type: string</c> properties (pointId, datetime, valueType, valueText) and a
    /// <c>type: boolean</c> one (valueBool), so such a test passes even if only the number branch
    /// survived.
    /// </summary>
    [Theory]
    [InlineData(typeof(TelemetryReading))]
    [InlineData(typeof(LatestSample))]
    public void Value_SerializesAsAOneOfUnion(Type dtoType)
    {
        var oneOf = OneOfBlockOf(SerializeSchemaFor(dtoType));

        Assert.Contains("\"number\"", oneOf);
        Assert.Contains("\"string\"", oneOf);
        Assert.Contains("\"boolean\"", oneOf);
    }

    /// <summary>
    /// Exactly one branch carries null. On the parent it would be invalid 3.0 (no sibling type); on
    /// all three it would make <c>null</c> match every branch, and <c>oneOf</c> means exactly one —
    /// a strict validator would then reject the <c>"value": null</c> this API returns for a point
    /// with no reading.
    /// </summary>
    [Fact]
    public void Value_MarksExactlyOneBranchNullable()
    {
        var oneOf = OneOfBlockOf(SerializeSchemaFor(typeof(TelemetryReading)));
        var nullableBranches = oneOf.Split("\"nullable\": true").Length - 1;

        // Exactly one, not "at least one": marking all three is precisely the regression this test
        // exists to catch, and >= 1 would wave it through.
        Assert.Equal(1, nullableBranches);
    }

    /// <summary>
    /// Extracts just the <c>value</c> property's <c>oneOf</c> array from the serialized schema, so
    /// assertions cannot be satisfied by unrelated properties of the same DTO.
    /// </summary>
    private static string OneOfBlockOf(string json)
    {
        var start = json.IndexOf("\"oneOf\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"no oneOf in the serialized schema:\n{json}");

        var open = json.IndexOf('[', start);
        var close = json.IndexOf(']', open);
        Assert.True(open >= 0 && close > open, $"malformed oneOf:\n{json}");

        return json[open..close];
    }

    /// <summary>
    /// The filter must match only the two telemetry DTOs' <c>Value</c>. A predicate keyed on
    /// <c>object?</c> would rewrite every such property in the document; this pins that it does not.
    /// </summary>
    [Fact]
    public void UnrelatedObjectProperty_IsLeftAlone()
    {
        var schema = new OpenApiSchema();
        var context = new SchemaFilterContext(
            typeof(object), null, new SchemaRepository(),
            memberInfo: typeof(Unrelated).GetProperty(nameof(Unrelated.Value)));

        new TelemetryValueSchemaFilter().Apply(schema, context);

        Assert.Empty(schema.OneOf ?? []);
    }

    /// <summary>
    /// Swashbuckle 10 also invokes filters with an <c>OpenApiSchemaReference</c>, which is not the
    /// mutable document node. Casting blindly throws at swagger-generation time — breaking
    /// <c>Tools/sync-type.bash</c> and the #354 CI step rather than a test — so the filter must
    /// tolerate it.
    /// </summary>
    [Fact]
    public void NonMutableSchema_IsIgnoredRatherThanThrowing()
    {
        var context = new SchemaFilterContext(
            typeof(object), null, new SchemaRepository(),
            memberInfo: typeof(TelemetryReading).GetProperty(nameof(TelemetryReading.Value)));

        var reference = new OpenApiSchemaReference("TelemetryReading");

        new TelemetryValueSchemaFilter().Apply(reference, context);
    }

    private sealed record Unrelated(object? Value);
}
