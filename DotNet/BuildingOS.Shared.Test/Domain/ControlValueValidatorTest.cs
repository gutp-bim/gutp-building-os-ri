using BuildingOS.Shared;
using BuildingOS.Shared.Domain;

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
}
