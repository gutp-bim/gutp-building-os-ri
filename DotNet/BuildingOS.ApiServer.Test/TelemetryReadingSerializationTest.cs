using BuildingOS.Shared;
using BuildingOs.ApiServer.Controllers;
using BuildingOs.ApiServer.Telemetry;
using System.Text.Json;

namespace BuildingOS.ApiServer.Test;

/// <summary>
/// What the union-typed <c>value</c> (#344) actually looks like on the wire.
///
/// <para>
/// The schema tests next door prove the OpenAPI document advertises <c>oneOf</c>; they say nothing
/// about the bytes. This asserts the other half — that a numeric point really ships <c>21.5</c>, a
/// multi-state point <c>"occupied"</c>, a binary point <c>true</c>, and a point with no reading
/// <c>null</c>. Those four cases were previously only ever going to be checked by a manual smoke
/// against a running API, which leaves no regression guard behind.
/// </para>
///
/// <para>
/// <see cref="Options"/> is <see cref="JsonSerializerDefaults.Web"/> because that is exactly what
/// this API serializes with: <c>Startup.cs</c> calls <c>.AddControllers()</c> with no
/// <c>.AddJsonOptions(...)</c> anywhere in the project, so MVC uses the stock Web defaults
/// (camelCase, nulls emitted). If someone later adds custom JSON options, this test keeps passing
/// while the real responses change — so pin the options here to the same thing MVC resolves, and
/// treat a divergence as a reason to revisit this file.
/// </para>
/// </summary>
public class TelemetryReadingSerializationTest
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    private static string ValueOf(string json) =>
        JsonDocument.Parse(json).RootElement.GetProperty("value").GetRawText();

    private static ValidTelemetryData Row(
        double? value = null, string? valueType = null, string? valueText = null, bool? valueBool = null) =>
        new()
        {
            PointId = "PT001",
            Datetime = "2026-01-01T00:00:00Z",
            Value = value,
            ValueType = valueType,
            ValueText = valueText,
            ValueBool = valueBool,
        };

    // ── TelemetryReading (GET /telemetries/query and the per-tier reads) ─────

    [Fact]
    public void NumericReading_SerializesAsANumber()
    {
        var json = JsonSerializer.Serialize(
            TelemetryReading.From(Row(value: 21.5, valueType: TelemetryValueKind.Number)), Options);

        Assert.Equal("21.5", ValueOf(json));
    }

    [Fact]
    public void StringReading_SerializesAsAQuotedString()
    {
        var json = JsonSerializer.Serialize(
            TelemetryReading.From(Row(valueType: TelemetryValueKind.String, valueText: "occupied")), Options);

        Assert.Equal("\"occupied\"", ValueOf(json));
    }

    [Theory]
    [InlineData(true, "true")]
    [InlineData(false, "false")]
    public void BooleanReading_SerializesAsABareBoolean(bool reading, string expected)
    {
        var json = JsonSerializer.Serialize(
            TelemetryReading.From(Row(valueType: TelemetryValueKind.Boolean, valueBool: reading)), Options);

        Assert.Equal(expected, ValueOf(json));
    }

    /// <summary>
    /// A point with no representable reading ships an explicit <c>null</c>, not an omitted property.
    /// The frontend's freshness logic distinguishes "no reading" from "field absent", and the
    /// document's oneOf marks exactly one branch nullable to describe this.
    /// </summary>
    [Fact]
    public void NoReading_SerializesAsAnExplicitNull()
    {
        var json = JsonSerializer.Serialize(TelemetryReading.From(Row()), Options);

        Assert.Equal("null", ValueOf(json));
    }

    /// <summary>
    /// An aggregate bucket carries both an average and a state representative; the union takes the
    /// numeric half (see <c>TelemetryValueKind.Resolve</c>). Pinned on the wire because getting this
    /// backwards would silently turn a Hour/Day series into text for any mixed point.
    /// </summary>
    [Fact]
    public void MixedAggregateBucket_SerializesTheAverage_NotTheStateRepresentative()
    {
        var json = JsonSerializer.Serialize(
            TelemetryReading.From(Row(value: 42, valueType: TelemetryValueKind.String, valueText: "auto")),
            Options);

        Assert.Equal("42", ValueOf(json));
        // The state half stays reachable — that is what the timeline reads.
        Assert.Equal("\"auto\"", JsonDocument.Parse(json).RootElement.GetProperty("valueText").GetRawText());
    }

    /// <summary>
    /// Dual-emit (#344 PR A): the legacy trio ships alongside the union so a client built against
    /// the old shape keeps working regardless of deploy order. #344 PR B removes these — this test
    /// is the thing that should fail then, deliberately.
    /// </summary>
    [Fact]
    public void LegacyDiscriminatedFields_AreStillEmittedAlongsideTheUnion()
    {
        var json = JsonSerializer.Serialize(
            TelemetryReading.From(Row(valueType: TelemetryValueKind.String, valueText: "occupied")), Options);
        var root = JsonDocument.Parse(json).RootElement;

        Assert.Equal("\"occupied\"", root.GetProperty("value").GetRawText());
        Assert.Equal("\"string\"", root.GetProperty("valueType").GetRawText());
        Assert.Equal("\"occupied\"", root.GetProperty("valueText").GetRawText());
        Assert.True(root.TryGetProperty("valueBool", out _));
    }

    [Fact]
    public void PropertyNames_AreCamelCase()
    {
        var json = JsonSerializer.Serialize(
            TelemetryReading.From(Row(value: 1, valueType: TelemetryValueKind.Number)), Options);

        Assert.Contains("\"pointId\"", json);
        Assert.Contains("\"valueType\"", json);
        Assert.DoesNotContain("\"PointId\"", json);
    }

    // ── LatestSample (POST /telemetries/query/batch-latest) ─────────────────

    [Theory]
    [InlineData(TelemetryValueKind.Number, "21.5")]
    [InlineData(TelemetryValueKind.String, "\"occupied\"")]
    [InlineData(TelemetryValueKind.Boolean, "true")]
    public void LatestSample_CarriesTheSameUnionEncoding(string kind, string expected)
    {
        object? value = kind switch
        {
            TelemetryValueKind.String => "occupied",
            TelemetryValueKind.Boolean => true,
            _ => 21.5d,
        };
        var json = JsonSerializer.Serialize(
            new LatestSample("PT001", "2026-01-01T00:00:00Z", value, kind), Options);

        Assert.Equal(expected, ValueOf(json));
    }

    [Fact]
    public void LatestSample_WithNoData_SerializesNullsRatherThanOmittingThem()
    {
        var json = JsonSerializer.Serialize(new LatestSample("PT001", null, null), Options);
        var root = JsonDocument.Parse(json).RootElement;

        Assert.Equal("null", root.GetProperty("datetime").GetRawText());
        Assert.Equal("null", root.GetProperty("value").GetRawText());
    }
}
