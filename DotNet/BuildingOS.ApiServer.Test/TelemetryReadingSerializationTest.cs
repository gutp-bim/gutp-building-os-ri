using BuildingOS.Shared;
using BuildingOs.ApiServer.Controllers;
using BuildingOs.ApiServer.Telemetry;
using System.Globalization;
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
/// <see cref="Options"/> is taken from <see cref="Microsoft.AspNetCore.Mvc.JsonOptions"/> rather
/// than being re-declared as <c>JsonSerializerDefaults.Web</c>. Re-declaring would defeat the
/// point: someone adding <c>.AddJsonOptions(o =&gt; o.JsonSerializerOptions.DefaultIgnoreCondition
/// = WhenWritingNull)</c> would make real responses omit <c>value</c> for a point with no reading
/// — contradicting what these tests pin — while every test here stayed green. Binding to MVC's own
/// options type means the defaults tracked here move when MVC's do.
/// </para>
/// </summary>
public class TelemetryReadingSerializationTest
{
    private static readonly JsonSerializerOptions Options =
        new Microsoft.AspNetCore.Mvc.JsonOptions().JsonSerializerOptions;

    private static string ValueOf(string json) =>
        JsonDocument.Parse(json).RootElement.GetProperty("value").GetRawText();

    private static string StateOf(string json) =>
        JsonDocument.Parse(json).RootElement.GetProperty("state").GetRawText();

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
    /// <para>
    /// The reason is the published contract, not the frontend: <c>TelemetryValueSchemaFilter</c>
    /// marks one <c>oneOf</c> branch nullable, so the document promises <c>null</c> is a value this
    /// field takes. Omitting the property instead would contradict that for any consumer validating
    /// against the schema. (The web client itself cannot tell the two apart —
    /// <c>resolveTelemetryValue</c> branches on <c>typeof</c>, so an absent property and an explicit
    /// null both resolve to <c>{ kind: "none" }</c> — so do not justify this by the frontend.)
    /// </para>
    /// </summary>
    [Fact]
    public void NoReading_SerializesAsAnExplicitNull()
    {
        var json = JsonSerializer.Serialize(TelemetryReading.From(Row()), Options);

        Assert.Equal("null", ValueOf(json));
    }

    // ── `state`: the non-numeric half, carried on its own (#359) ────────────

    /// <summary>
    /// The reason <c>state</c> exists at all, and the reason the union takes the numeric half.
    /// <para>
    /// A mixed aggregate bucket is the one row shape carrying two readings at once — the average in
    /// <c>value</c> (see <c>TelemetryValueKind.Resolve</c>) and a last-in-bucket state representative
    /// beside it. Getting the union backwards would silently turn a Hour/Day series into text;
    /// dropping <c>valueText</c>/<c>valueBool</c> without a replacement carrier would leave the
    /// representative nowhere to live and empty the state timeline at that granularity.
    /// </para>
    /// </summary>
    [Fact]
    public void MixedAggregateBucket_SerializesTheAverageAndCarriesTheStateSeparately()
    {
        var json = JsonSerializer.Serialize(
            TelemetryReading.From(Row(value: 42, valueType: TelemetryValueKind.String, valueText: "auto")),
            Options);

        Assert.Equal("42", ValueOf(json));
        Assert.Equal("\"auto\"", StateOf(json));
    }

    [Fact]
    public void MixedAggregateBucket_CarriesABooleanStateRepresentative()
    {
        var json = JsonSerializer.Serialize(
            TelemetryReading.From(Row(value: 42, valueType: TelemetryValueKind.Boolean, valueBool: true)),
            Options);

        Assert.Equal("42", ValueOf(json));
        Assert.Equal("true", StateOf(json));
    }

    /// <summary>
    /// A raw non-numeric row repeats its reading in <c>state</c> rather than leaving it null.
    /// <para>
    /// The duplication is deliberate. It makes <c>state</c> mean one thing unconditionally — "the
    /// non-numeric reading this row carries, if any" — so the client's state resolver is a single
    /// lookup with no fallback. The alternative (populate <c>state</c> only when it differs from
    /// <c>value</c>) saves a few bytes and rebuilds the very fallback chain #359 exists to delete.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(TelemetryValueKind.String, "occupied", null, "\"occupied\"")]
    [InlineData(TelemetryValueKind.Boolean, null, false, "false")]
    public void RawNonNumericReading_RepeatsItsReadingInState(
        string kind, string? text, bool? boolean, string expected)
    {
        var json = JsonSerializer.Serialize(
            TelemetryReading.From(Row(valueType: kind, valueText: text, valueBool: boolean)), Options);

        Assert.Equal(expected, ValueOf(json));
        Assert.Equal(expected, StateOf(json));
    }

    /// <summary>A purely numeric row has no state half — the timeline must not invent one.</summary>
    [Fact]
    public void NumericReading_HasNoState()
    {
        var json = JsonSerializer.Serialize(
            TelemetryReading.From(Row(value: 21.5, valueType: TelemetryValueKind.Number)), Options);

        Assert.Equal("null", StateOf(json));
    }

    [Fact]
    public void NoReading_HasNoState()
    {
        Assert.Equal("null", StateOf(JsonSerializer.Serialize(TelemetryReading.From(Row()), Options)));
    }

    /// <summary>
    /// #359: <c>valueText</c>/<c>valueBool</c> are gone from the wire, replaced by <c>state</c>.
    /// <para>
    /// <c>valueType</c> deliberately stays. It does not duplicate the payload the way the other two
    /// did — it describes <c>value</c>, and is derived from the value actually shipped.
    /// </para>
    /// </summary>
    [Fact]
    public void LegacyPayloadFields_AreGoneFromTheWire()
    {
        var json = JsonSerializer.Serialize(
            TelemetryReading.From(Row(valueType: TelemetryValueKind.String, valueText: "occupied")), Options);
        var root = JsonDocument.Parse(json).RootElement;

        Assert.False(root.TryGetProperty("valueText", out _));
        Assert.False(root.TryGetProperty("valueBool", out _));
        Assert.Equal("\"occupied\"", root.GetProperty("value").GetRawText());
        Assert.Equal("\"string\"", root.GetProperty("valueType").GetRawText());
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

    /// <summary>
    /// <c>LatestSample</c> carries <c>state</c> too, so the two telemetry wire DTOs stay the same
    /// shape — <c>TelemetryWireValue</c> on the client is structurally satisfied by both, and a
    /// divergence here would only surface as a type error in the generated client.
    /// </summary>
    [Fact]
    public void LatestSample_CarriesTheStateHalfAsWell()
    {
        var json = JsonSerializer.Serialize(
            new LatestSample("PT001", "2026-01-01T00:00:00Z", "occupied",
                TelemetryValueKind.String, "occupied"),
            Options);

        Assert.Equal("\"occupied\"", StateOf(json));
    }

    [Fact]
    public void LatestSample_DropsTheLegacyPayloadFields()
    {
        var root = JsonDocument.Parse(JsonSerializer.Serialize(
            new LatestSample("PT001", null, null), Options)).RootElement;

        Assert.False(root.TryGetProperty("valueText", out _));
        Assert.False(root.TryGetProperty("valueBool", out _));
    }

    [Fact]
    public void LatestSample_WithNoData_SerializesNullsRatherThanOmittingThem()
    {
        var json = JsonSerializer.Serialize(new LatestSample("PT001", null, null), Options);
        var root = JsonDocument.Parse(json).RootElement;

        Assert.Equal("null", root.GetProperty("datetime").GetRawText());
        Assert.Equal("null", root.GetProperty("value").GetRawText());
    }

    // ── valueType describes `value`, not something beside it ────────────────

    /// <summary>
    /// The wire's <c>valueType</c> must describe the <c>value</c> it ships with.
    /// <para>
    /// It did not for a mixed aggregate bucket: the store tags the bucket by its <i>last-in-bucket</i>
    /// reading, so a mixed hour arrived as <c>{ value: 42, valueType: "string", valueText: "auto" }</c>
    /// — the discriminant describing <c>valueText</c> while <c>value</c> is a number. Harmless only
    /// because neither resolver consults it (both are numeric-first); a consumer that trusted the
    /// discriminant would mis-read the field.
    /// </para>
    /// </summary>
    [Fact]
    public void MixedAggregateBucket_TagsTheDiscriminantForTheNumericValue()
    {
        var json = JsonSerializer.Serialize(
            TelemetryReading.From(Row(value: 42, valueType: TelemetryValueKind.String, valueText: "auto")),
            Options);
        var root = JsonDocument.Parse(json).RootElement;

        Assert.Equal("42", root.GetProperty("value").GetRawText());
        Assert.Equal("\"number\"", root.GetProperty("valueType").GetRawText());
        // The state representative is still reachable — the timeline reads it.
        Assert.Equal("\"auto\"", root.GetProperty("state").GetRawText());
    }

    [Theory]
    [InlineData("21.5", null, null, "\"number\"")]
    [InlineData(null, "occupied", null, "\"string\"")]
    [InlineData(null, null, true, "\"boolean\"")]
    public void ValueType_AlwaysMatchesTheRuntimeTypeOfValue(
        string? numeric, string? text, bool? boolean, string expected)
    {
        var row = Row(
            value: numeric is null ? null : double.Parse(numeric, CultureInfo.InvariantCulture),
            valueText: text,
            valueBool: boolean);

        var json = JsonSerializer.Serialize(TelemetryReading.From(row), Options);

        Assert.Equal(expected, JsonDocument.Parse(json).RootElement.GetProperty("valueType").GetRawText());
    }

    [Fact]
    public void NoReading_LeavesTheDiscriminantNull()
    {
        var json = JsonSerializer.Serialize(TelemetryReading.From(Row()), Options);

        Assert.Equal("null", JsonDocument.Parse(json).RootElement.GetProperty("valueType").GetRawText());
    }
}
