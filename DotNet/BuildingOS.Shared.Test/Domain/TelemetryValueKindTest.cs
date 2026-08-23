using BuildingOS.Shared;
using System.Text.Json;

namespace BuildingOS.Shared.Test.Domain;

/// <summary>
/// Characterization tests for the discriminated-value mapping (#152, ADR-0006). This is the pure core
/// that decides which of <c>Value</c>/<c>ValueText</c>/<c>ValueBool</c> a telemetry reading lands in,
/// and it had no direct coverage — its behaviour was only exercised incidentally through the store
/// tests. The frontend's resolution precedence (`web-client/src/lib/telemetry/value.ts`) leans on the
/// invariants pinned here, in particular
/// <see cref="Apply_SetsExactlyOnePayloadField_AndAlwaysTagsTheDiscriminant"/>.
/// </summary>
public class TelemetryValueKindTest
{
    // JsonSerializer.Deserialize, not JsonDocument.Parse().RootElement: the latter is backed by a
    // pooled buffer that is recycled when the JsonDocument is disposed, so the element would read
    // garbage once it escapes the using-scope.
    private static JsonElement Json(string raw) => JsonSerializer.Deserialize<JsonElement>(raw);

    // ── Apply ────────────────────────────────────────────────────────────────

    [Fact]
    public void Apply_Number_SetsValueAndNumberDiscriminant()
    {
        var target = new ValidTelemetryData();
        Assert.True(TelemetryValueKind.Apply(target, Json("21.5")));

        Assert.Equal(21.5, target.Value);
        Assert.Equal(TelemetryValueKind.Number, target.ValueType);
        Assert.Null(target.ValueText);
        Assert.Null(target.ValueBool);
    }

    [Fact]
    public void Apply_String_SetsValueTextAndStringDiscriminant()
    {
        var target = new ValidTelemetryData();
        Assert.True(TelemetryValueKind.Apply(target, Json("\"auto\"")));

        Assert.Equal("auto", target.ValueText);
        Assert.Equal(TelemetryValueKind.String, target.ValueType);
        Assert.Null(target.Value);
        Assert.Null(target.ValueBool);
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    public void Apply_Boolean_SetsValueBoolAndBooleanDiscriminant(string raw, bool expected)
    {
        var target = new ValidTelemetryData();
        Assert.True(TelemetryValueKind.Apply(target, Json(raw)));

        Assert.Equal(expected, target.ValueBool);
        Assert.Equal(TelemetryValueKind.Boolean, target.ValueType);
        Assert.Null(target.Value);
        Assert.Null(target.ValueText);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void Apply_NonRepresentableKind_LeavesEveryFieldUnsetAndReturnsFalse(string raw)
    {
        var target = new ValidTelemetryData();
        Assert.False(TelemetryValueKind.Apply(target, Json(raw)));

        Assert.Null(target.Value);
        Assert.Null(target.ValueType);
        Assert.Null(target.ValueText);
        Assert.Null(target.ValueBool);
    }

    /// <summary>
    /// The cross-language contract: a representable value always tags the discriminant AND populates
    /// exactly one payload field. The web client relies on both halves — it trusts `valueType` when
    /// present, and treats a populated `valueText`/`valueBool` as the stronger signal when it is
    /// absent (only pre-#152 legacy rows arrive untagged, and those carry no text/bool at all).
    /// </summary>
    [Theory]
    [InlineData("21.5")]
    [InlineData("\"auto\"")]
    [InlineData("true")]
    [InlineData("false")]
    public void Apply_SetsExactlyOnePayloadField_AndAlwaysTagsTheDiscriminant(string raw)
    {
        var target = new ValidTelemetryData();
        Assert.True(TelemetryValueKind.Apply(target, Json(raw)));

        Assert.NotNull(target.ValueType);
        var populated = new bool[]
        {
            target.Value.HasValue,
            target.ValueText is not null,
            target.ValueBool.HasValue,
        }.Count(set => set);
        Assert.Equal(1, populated);
    }

    // ── Resolve (union projection for the API wire, #344) ────────────────────

    /// <summary>
    /// The API returns a single union-typed <c>value</c> (#344). This projects the stored
    /// discriminated fields back onto that one value, and must agree with the client-side resolver
    /// in <c>web-client/src/lib/telemetry/value.ts</c> — two implementations that disagree about a
    /// row is exactly the failure mode the single-decode-point work (#346) removed.
    /// </summary>
    [Fact]
    public void Resolve_Number_YieldsTheDouble()
        => Assert.Equal(21.5, TelemetryValueKind.Resolve(
            new ValidTelemetryData { ValueType = TelemetryValueKind.Number, Value = 21.5 }));

    [Fact]
    public void Resolve_String_YieldsTheText()
        => Assert.Equal("auto", TelemetryValueKind.Resolve(
            new ValidTelemetryData { ValueType = TelemetryValueKind.String, ValueText = "auto" }));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Resolve_Boolean_YieldsTheBool(bool v)
        => Assert.Equal(v, TelemetryValueKind.Resolve(
            new ValidTelemetryData { ValueType = TelemetryValueKind.Boolean, ValueBool = v }));

    /// <summary>A legacy row (pre-#152) carries only Value and no discriminant.</summary>
    [Fact]
    public void Resolve_LegacyNumericWithoutDiscriminant_YieldsTheDouble()
        => Assert.Equal(0d, TelemetryValueKind.Resolve(new ValidTelemetryData { Value = 0 }));

    /// <summary>
    /// <b>A mixed aggregate bucket carries two values at once</b>, and the numeric one wins the union.
    /// <c>AggregatingParquetTelemetryStore.ToTelemetry</c> sets <c>Value = Avg</c> unconditionally
    /// (the Timescale continuous-aggregate contract) while tagging the bucket by its
    /// <i>last-in-bucket</i> reading — so an hour containing numeric samples whose last reading was a
    /// string really is <c>{ Value = 42, ValueType = "string", ValueText = "auto" }</c> on the wire.
    /// <para>
    /// Collapsing that onto one <c>value</c> is lossy either way, so it resolves to the average:
    /// <c>value</c> has a published numeric meaning for Hour/Day granularity that charts and external
    /// consumers already depend on, whereas the state representative remains reachable through
    /// <c>valueText</c>/<c>valueBool</c>. Returning the string here would silently turn an aggregate
    /// series into text.
    /// </para>
    /// </summary>
    [Fact]
    public void Resolve_MixedAggregateBucket_YieldsTheNumericAverageNotTheStateRepresentative()
    {
        Assert.Equal(42d, TelemetryValueKind.Resolve(new ValidTelemetryData
        {
            Value = 42, ValueType = TelemetryValueKind.String, ValueText = "auto",
        }));
        Assert.Equal(42d, TelemetryValueKind.Resolve(new ValidTelemetryData
        {
            Value = 42, ValueType = TelemetryValueKind.Boolean, ValueBool = true,
        }));
    }

    /// <summary>A purely non-numeric bucket has no average, so the representative is the value.</summary>
    [Fact]
    public void Resolve_NonNumericAggregateBucket_YieldsTheRepresentative()
        => Assert.Equal("auto", TelemetryValueKind.Resolve(new ValidTelemetryData
        {
            Value = null, ValueType = TelemetryValueKind.String, ValueText = "auto",
        }));

    [Fact]
    public void Resolve_UntaggedWithText_AndNoNumber_YieldsTheText()
        => Assert.Equal("auto", TelemetryValueKind.Resolve(
            new ValidTelemetryData { ValueText = "auto" }));

    [Fact]
    public void Resolve_UntaggedWithBool_AndNoNumber_YieldsTheBool()
        => Assert.Equal(false, TelemetryValueKind.Resolve(
            new ValidTelemetryData { ValueBool = false }));

    /// <summary>Tagged string with neither a number nor text is not representable.</summary>
    [Fact]
    public void Resolve_TaggedStringWithNothingToShow_YieldsNull()
        => Assert.Null(TelemetryValueKind.Resolve(
            new ValidTelemetryData { ValueType = TelemetryValueKind.String }));

    [Fact]
    public void Resolve_NothingRepresentable_YieldsNull()
    {
        Assert.Null(TelemetryValueKind.Resolve(new ValidTelemetryData()));
        Assert.Null(TelemetryValueKind.Resolve(null));
    }

    // ── KindOf (the wire discriminant, #359) ─────────────────────────────────

    [Fact]
    public void KindOf_MatchesTheRuntimeTypeOfAResolvedValue()
    {
        Assert.Equal(TelemetryValueKind.Number, TelemetryValueKind.KindOf(21.5));
        Assert.Equal(TelemetryValueKind.String, TelemetryValueKind.KindOf("auto"));
        Assert.Equal(TelemetryValueKind.Boolean, TelemetryValueKind.KindOf(true));
        Assert.Null(TelemetryValueKind.KindOf(null));
    }

    /// <summary>
    /// An unrecognized type yields <c>null</c> — an unknown kind, not a wrong one. A catch-all
    /// <c>_ =&gt; Number</c> would label a <see cref="JsonElement"/> (what <c>object?</c> deserializes
    /// back into, so what any re-projection of an already-parsed response hands this) as
    /// <c>"number"</c> even when it holds a string — silently reintroducing the contradiction #359
    /// removed.
    /// </summary>
    [Fact]
    public void KindOf_UnrecognizedType_YieldsNullRatherThanGuessingNumber()
    {
        Assert.Null(TelemetryValueKind.KindOf(Json("\"auto\"")));
        Assert.Null(TelemetryValueKind.KindOf(Json("true")));
        Assert.Null(TelemetryValueKind.KindOf(new object()));
    }

    /// <summary>Round-trip with <see cref="Resolve"/>: the pair must never disagree.</summary>
    [Theory]
    [InlineData(21.5, null, null, TelemetryValueKind.Number)]
    [InlineData(null, "auto", null, TelemetryValueKind.String)]
    [InlineData(null, null, true, TelemetryValueKind.Boolean)]
    [InlineData(null, null, null, null)]
    public void KindOf_AgreesWithResolve(double? value, string? text, bool? boolean, string? expected)
    {
        var row = new ValidTelemetryData { Value = value, ValueText = text, ValueBool = boolean };

        Assert.Equal(expected, TelemetryValueKind.KindOf(TelemetryValueKind.Resolve(row)));
    }

    // ── ResolveLastInBucket ──────────────────────────────────────────────────

    [Fact]
    public void ResolveLastInBucket_StringLast_YieldsStringAndText()
    {
        var last = new ValidTelemetryData { ValueType = TelemetryValueKind.String, ValueText = "auto" };
        Assert.Equal((TelemetryValueKind.String, "auto", (bool?)null),
            TelemetryValueKind.ResolveLastInBucket(last, hasNumeric: false));
    }

    [Fact]
    public void ResolveLastInBucket_BooleanLast_YieldsBooleanAndBool()
    {
        var last = new ValidTelemetryData { ValueType = TelemetryValueKind.Boolean, ValueBool = false };
        Assert.Equal((TelemetryValueKind.Boolean, (string?)null, (bool?)false),
            TelemetryValueKind.ResolveLastInBucket(last, hasNumeric: false));
    }

    /// <summary>A non-numeric last reading wins even when the bucket also contained numeric rows.</summary>
    [Fact]
    public void ResolveLastInBucket_StringLast_IgnoresHasNumeric()
    {
        var last = new ValidTelemetryData { ValueType = TelemetryValueKind.String, ValueText = "manual" };
        Assert.Equal((TelemetryValueKind.String, "manual", (bool?)null),
            TelemetryValueKind.ResolveLastInBucket(last, hasNumeric: true));
    }

    [Fact]
    public void ResolveLastInBucket_NumericLast_WithNumericInBucket_YieldsNumber()
    {
        var last = new ValidTelemetryData { ValueType = TelemetryValueKind.Number, Value = 21.5 };
        Assert.Equal((TelemetryValueKind.Number, (string?)null, (bool?)null),
            TelemetryValueKind.ResolveLastInBucket(last, hasNumeric: true));
    }

    [Fact]
    public void ResolveLastInBucket_NullLast_WithNumericInBucket_YieldsNumber()
        => Assert.Equal((TelemetryValueKind.Number, (string?)null, (bool?)null),
            TelemetryValueKind.ResolveLastInBucket(null, hasNumeric: true));

    /// <summary>An empty / non-representable bucket stays untagged rather than claiming "number".</summary>
    [Fact]
    public void ResolveLastInBucket_NullLast_WithoutNumeric_YieldsUntagged()
        => Assert.Equal(((string?)null, (string?)null, (bool?)null),
            TelemetryValueKind.ResolveLastInBucket(null, hasNumeric: false));

    // ── IsLaterInBucket ──────────────────────────────────────────────────────

    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void IsLaterInBucket_StrictlyLaterTimestamp_IsLater()
        => Assert.True(TelemetryValueKind.IsLaterInBucket(
            T0.AddSeconds(1), new ValidTelemetryData { Id = "a" },
            T0, new ValidTelemetryData { Id = "z" }));

    [Fact]
    public void IsLaterInBucket_EarlierTimestamp_IsNotLater()
        => Assert.False(TelemetryValueKind.IsLaterInBucket(
            T0, new ValidTelemetryData { Id = "z" },
            T0.AddSeconds(1), new ValidTelemetryData { Id = "a" }));

    /// <summary>The Id tiebreaker makes the pick order-independent when timestamps collide (#152 D3).</summary>
    [Fact]
    public void IsLaterInBucket_SameTimestamp_GreaterIdWins()
    {
        Assert.True(TelemetryValueKind.IsLaterInBucket(
            T0, new ValidTelemetryData { Id = "b" }, T0, new ValidTelemetryData { Id = "a" }));
        Assert.False(TelemetryValueKind.IsLaterInBucket(
            T0, new ValidTelemetryData { Id = "a" }, T0, new ValidTelemetryData { Id = "b" }));
    }

    [Fact]
    public void IsLaterInBucket_SameTimestampAndId_IsNotLater()
        => Assert.False(TelemetryValueKind.IsLaterInBucket(
            T0, new ValidTelemetryData { Id = "a" }, T0, new ValidTelemetryData { Id = "a" }));

    /// <summary>A null "best" has no Id, so any row with one is later at the same timestamp.</summary>
    [Fact]
    public void IsLaterInBucket_AgainstNullBest_TreatsMissingIdAsEmptyOrdinal()
    {
        Assert.True(TelemetryValueKind.IsLaterInBucket(
            T0, new ValidTelemetryData { Id = "a" }, T0, null));
        Assert.False(TelemetryValueKind.IsLaterInBucket(
            T0, new ValidTelemetryData { Id = null }, T0, null));
    }
}
