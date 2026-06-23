using ParityHarness.Core;
using Xunit;

namespace ParityHarness.Test.Core;

public class JsonDiffTest
{
    [Fact]
    public void Identical_Json_Returns_No_Diffs()
    {
        var json = """{"a":1,"b":"hello","c":true}""";
        var diffs = JsonDiff.Compare(json, json);
        Assert.Empty(diffs);
    }

    [Fact]
    public void Value_Mismatch_Returns_Diff()
    {
        var expected = """{"temperature":22.5}""";
        var actual   = """{"temperature":23.0}""";
        var diffs = JsonDiff.Compare(expected, actual);
        Assert.Single(diffs);
        Assert.Equal("temperature", diffs[0].Path);
        Assert.Equal(DiffType.ValueMismatch, diffs[0].Type);
        Assert.Equal("22.5", diffs[0].Expected);
        Assert.Equal("23.0", diffs[0].Actual);
    }

    [Fact]
    public void Missing_Field_In_Actual_Returns_Diff()
    {
        var expected = """{"a":1,"b":2}""";
        var actual   = """{"a":1}""";
        var diffs = JsonDiff.Compare(expected, actual);
        Assert.Single(diffs);
        Assert.Equal("b", diffs[0].Path);
        Assert.Equal(DiffType.Missing, diffs[0].Type);
    }

    [Fact]
    public void Extra_Field_In_Actual_Returns_Diff()
    {
        var expected = """{"a":1}""";
        var actual   = """{"a":1,"b":2}""";
        var diffs = JsonDiff.Compare(expected, actual);
        Assert.Single(diffs);
        Assert.Equal("b", diffs[0].Path);
        Assert.Equal(DiffType.Extra, diffs[0].Type);
    }

    [Fact]
    public void Nested_Object_Diff_Reports_Full_Path()
    {
        var expected = """{"sensor":{"value":10,"unit":"C"}}""";
        var actual   = """{"sensor":{"value":11,"unit":"C"}}""";
        var diffs = JsonDiff.Compare(expected, actual);
        Assert.Single(diffs);
        Assert.Equal("sensor.value", diffs[0].Path);
    }

    [Fact]
    public void Array_Length_Mismatch_Returns_Diff()
    {
        var expected = """{"items":[1,2,3]}""";
        var actual   = """{"items":[1,2]}""";
        var diffs = JsonDiff.Compare(expected, actual);
        var lengthDiff = diffs.FirstOrDefault(d => d.Type == DiffType.LengthMismatch);
        Assert.NotNull(lengthDiff);
        Assert.Equal("items", lengthDiff!.Path);
    }

    [Fact]
    public void Array_Element_Value_Mismatch_Reports_Index_Path()
    {
        var expected = """{"items":[{"v":1},{"v":2}]}""";
        var actual   = """{"items":[{"v":1},{"v":9}]}""";
        var diffs = JsonDiff.Compare(expected, actual);
        Assert.Single(diffs);
        Assert.Equal("items[1].v", diffs[0].Path);
    }

    [Fact]
    public void Type_Mismatch_Returns_Diff()
    {
        var expected = """{"count":5}""";
        var actual   = """{"count":"five"}""";
        var diffs = JsonDiff.Compare(expected, actual);
        Assert.Single(diffs);
        Assert.Equal(DiffType.TypeMismatch, diffs[0].Type);
    }

    [Fact]
    public void Null_Vs_Value_Returns_Diff()
    {
        var expected = """{"value":null}""";
        var actual   = """{"value":42}""";
        var diffs = JsonDiff.Compare(expected, actual);
        Assert.Single(diffs);
    }

    [Fact]
    public void Deep_Nested_Path_Is_Correct()
    {
        var expected = """{"a":{"b":{"c":{"d":1}}}}""";
        var actual   = """{"a":{"b":{"c":{"d":2}}}}""";
        var diffs = JsonDiff.Compare(expected, actual);
        Assert.Single(diffs);
        Assert.Equal("a.b.c.d", diffs[0].Path);
    }
}
