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
        Assert.Contains("gw", r0.Data); // raw json preserved
        Assert.Null(rows[1].Id);
        Assert.Equal(1, rows[1].Value);
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
    public void Parse_NonNumericValue_IsNull()
    {
        const string json = """{ "telemetries": [ { "point_id": "p", "value": "NaN", "datetime": "2026-06-12T12:00:00Z" } ] }""";
        var rows = ValidTelemetryEnvelope.Parse(json);
        Assert.Null(rows[0].Value);
    }
}
