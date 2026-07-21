using BuildingOS.Shared.Infrastructure.Telemetry.ParquetLake;

namespace BuildingOS.Shared.Test.Infrastructure.Telemetry.ParquetLake;

public class ValidTelemetryEnvelopeTest
{
    [Fact]
    public void Parse_ExpandsTelemetriesArray_AllFields()
    {
        const string json = """
        {
          "telemetries": [
            {
              "id": "e1", "point_id": "p1", "device_id": "d1", "building": "b1",
              "name": "temp", "value": 23.5, "datetime": "2026-06-12T12:00:00Z",
              "data": { "gw": "g1" }
            },
            { "point_id": "p2", "value": 1, "datetime": "2026-06-12T12:01:00Z" }
          ]
        }
        """;

        var rows = ValidTelemetryEnvelope.Parse(json);

        Assert.Equal(2, rows.Count);
        var r0 = rows[0];
        Assert.Equal("e1", r0.Id);
        Assert.Equal("p1", r0.PointId);
        Assert.Equal("b1", r0.Building);
        Assert.Equal(23.5, r0.Value);
        Assert.Equal("number", r0.ValueType); // #152: numeric rows carry the discriminant
        Assert.Contains("gw", r0.Data); // raw json preserved
        Assert.Null(rows[1].Id);
        Assert.Equal(1, rows[1].Value);
        Assert.Equal("number", rows[1].ValueType);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"telemetries\": null}")]
    [InlineData("{\"telemetries\": {}}")]
    public void Parse_MalformedOrMissing_ReturnsEmpty(string json)
    {
        Assert.Empty(ValidTelemetryEnvelope.Parse(json));
    }

    [Fact]
    public void Parse_StringValue_PopulatesValueText_NumericStaysNull()
    {
        // #152: a string reading no longer drops to null — it lands in ValueText with a "string"
        // discriminant, while the numeric Value column stays null (numeric-only).
        const string json = """{ "telemetries": [ { "point_id": "p", "value": "auto", "datetime": "2026-06-12T12:00:00Z" } ] }""";
        var rows = ValidTelemetryEnvelope.Parse(json);
        Assert.Null(rows[0].Value);
        Assert.Equal("string", rows[0].ValueType);
        Assert.Equal("auto", rows[0].ValueText);
        Assert.Null(rows[0].ValueBool);
    }

    [Fact]
    public void Parse_BooleanValue_PopulatesValueBool_NumericStaysNull()
    {
        const string json = """{ "telemetries": [ { "point_id": "p", "value": true, "datetime": "2026-06-12T12:00:00Z" } ] }""";
        var rows = ValidTelemetryEnvelope.Parse(json);
        Assert.Null(rows[0].Value);
        Assert.Equal("boolean", rows[0].ValueType);
        Assert.True(rows[0].ValueBool);
        Assert.Null(rows[0].ValueText);
    }

    [Fact]
    public void Parse_NonRepresentableValue_LeavesAllUnset()
    {
        // A null/object/array value is neither numeric nor a first-class string/boolean → all value
        // fields stay unset (the row is retained but carries no value), matching prior drop behavior.
        const string json = """{ "telemetries": [ { "point_id": "p", "value": null, "datetime": "2026-06-12T12:00:00Z" } ] }""";
        var rows = ValidTelemetryEnvelope.Parse(json);
        Assert.Null(rows[0].Value);
        Assert.Null(rows[0].ValueType);
        Assert.Null(rows[0].ValueText);
        Assert.Null(rows[0].ValueBool);
    }
}
