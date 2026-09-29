using BuildingOS.Shared;
using BuildingOS.Shared.Domain;
using BuildingOS.Shared.Domain.TwinAdmin;

namespace BuildingOS.Shared.Test.Domain;

public class ControlValueValidatorTest
{
    // ── boolean ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public void Boolean_Accepts_ZeroAndOne(double value)
        => Assert.True(ControlValueValidator.Validate(new ControlSchema { DataType = "boolean" }, value).IsValid);

    [Theory]
    [InlineData(2)]
    [InlineData(-1)]
    [InlineData(0.5)]
    public void Boolean_Rejects_NonBinary(double value)
        => Assert.False(ControlValueValidator.Validate(new ControlSchema { DataType = "boolean" }, value).IsValid);

    // ── enum ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Enum_Accepts_AllowedCodes(double value)
        => Assert.True(ControlValueValidator.Validate(
            new ControlSchema { DataType = "enum", EnumLabels = """{"1":"冷房","2":"暖房"}""" }, value).IsValid);

    [Fact]
    public void Enum_Rejects_CodeNotInLabels()
        => Assert.False(ControlValueValidator.Validate(
            new ControlSchema { DataType = "enum", EnumLabels = """{"1":"冷房","2":"暖房"}""" }, 3).IsValid);

    [Fact]
    public void Enum_WithoutLabels_IsPermissive()
        // No allowed set to check against → cannot validate, so do not block.
        => Assert.True(ControlValueValidator.Validate(new ControlSchema { DataType = "enum" }, 99).IsValid);

    // ── number range ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData(16)]
    [InlineData(20)]
    [InlineData(30)]
    public void Number_Accepts_WithinRange(double value)
        => Assert.True(ControlValueValidator.Validate(
            new ControlSchema { DataType = "number", MinValue = 16, MaxValue = 30 }, value).IsValid);

    [Theory]
    [InlineData(15.9)]
    [InlineData(30.1)]
    public void Number_Rejects_OutOfRange(double value)
        => Assert.False(ControlValueValidator.Validate(
            new ControlSchema { DataType = "number", MinValue = 16, MaxValue = 30 }, value).IsValid);

    [Fact]
    public void Number_NoBounds_AcceptsAnything()
        => Assert.True(ControlValueValidator.Validate(new ControlSchema { DataType = "number" }, 1e9).IsValid);

    [Fact]
    public void Number_OnlyMin_EnforcesLowerBound()
    {
        var schema = new ControlSchema { DataType = "number", MinValue = 0 };
        Assert.True(ControlValueValidator.Validate(schema, 0).IsValid);
        Assert.False(ControlValueValidator.Validate(schema, -0.1).IsValid);
    }

    // ── misc ─────────────────────────────────────────────────────────────────

    [Fact]
    public void UnknownDataType_IsPermissive()
        => Assert.True(ControlValueValidator.Validate(new ControlSchema { DataType = "weird" }, 42).IsValid);

    [Fact]
    public void DataType_IsCaseInsensitive()
        => Assert.False(ControlValueValidator.Validate(new ControlSchema { DataType = "Boolean" }, 5).IsValid);

    [Fact]
    public void InvalidResult_CarriesAnError()
    {
        var result = ControlValueValidator.Validate(new ControlSchema { DataType = "boolean" }, 7);
        Assert.False(result.IsValid);
        Assert.False(string.IsNullOrEmpty(result.Error));
    }

    // ── unusable schema (#481) ───────────────────────────────────────────────

    public static TheoryData<ControlSchema?, string> UnusableSchemas() => new()
    {
        { null, ControlSchemaIssueReasons.NoSchema },
        { new ControlSchema(), ControlSchemaIssueReasons.MissingDataType },
        { new ControlSchema { DataType = "  " }, ControlSchemaIssueReasons.MissingDataType },
        { new ControlSchema { DataType = "string" }, ControlSchemaIssueReasons.UnknownDataType },
        { new ControlSchema { DataType = "enum" }, ControlSchemaIssueReasons.MalformedEnumLabels },
        { new ControlSchema { DataType = "enum", EnumLabels = "[1,2]" }, ControlSchemaIssueReasons.MalformedEnumLabels },
        { new ControlSchema { DataType = "enum", EnumLabels = "{not json" }, ControlSchemaIssueReasons.MalformedEnumLabels },
        { new ControlSchema { DataType = "enum", EnumLabels = "{}" }, ControlSchemaIssueReasons.MalformedEnumLabels },
    };

    [Theory]
    [MemberData(nameof(UnusableSchemas))]
    public void UnusableReason_NamesWhyTheSchemaCannotConstrainAValue(ControlSchema? schema, string expected)
        => Assert.Equal(expected, ControlValueValidator.UnusableReason(schema));

    public static TheoryData<ControlSchema> UsableSchemas() => new()
    {
        new ControlSchema { DataType = "boolean" },
        new ControlSchema { DataType = "Number" },
        // Bounds are optional for a number: an unbounded setpoint is a modelling choice, not a failure.
        new ControlSchema { DataType = "number" },
        new ControlSchema { DataType = "enum", EnumLabels = """{"1":"cool"}""" },
    };

    [Theory]
    [MemberData(nameof(UsableSchemas))]
    public void UnusableReason_IsNull_ForAUsableSchema(ControlSchema schema)
        => Assert.Null(ControlValueValidator.UnusableReason(schema));

    [Theory]
    [InlineData(null, ControlSchemaFailurePolicy.Allow)]
    [InlineData("", ControlSchemaFailurePolicy.Allow)]
    [InlineData("allow", ControlSchemaFailurePolicy.Allow)]
    [InlineData("deny", ControlSchemaFailurePolicy.Deny)]
    [InlineData(" DENY ", ControlSchemaFailurePolicy.Deny)]
    public void ControlSchemaFailurePolicy_Parses(string? raw, ControlSchemaFailurePolicy expected)
        => Assert.Equal(expected, ControlSafetyOptions.ParsePolicy(raw, out _));

    [Fact]
    public void ControlSchemaFailurePolicy_UnknownValue_FallsBackToAllow_AndSaysSo()
    {
        Assert.Equal(ControlSchemaFailurePolicy.Allow, ControlSafetyOptions.ParsePolicy("block", out var recognized));
        Assert.False(recognized);
    }
}
