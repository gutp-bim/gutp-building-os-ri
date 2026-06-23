using ParityHarness.Core;
using ParityHarness.Report;
using Xunit;

namespace ParityHarness.Test.Report;

public class HtmlReporterTest
{
    [Fact]
    public void Html_Contains_Scenario_Names()
    {
        var results = new[]
        {
            new ParityResult("connector-hvac", []),
            new ParityResult("connector-bacnet", [new FieldDiff("value", "1", "2", DiffType.ValueMismatch)]),
        };
        var html = HtmlReporter.Generate(results);
        Assert.Contains("connector-hvac", html);
        Assert.Contains("connector-bacnet", html);
    }

    [Fact]
    public void Html_Is_Valid_Structure()
    {
        var html = HtmlReporter.Generate([new ParityResult("test", [])]);
        Assert.StartsWith("<!DOCTYPE html>", html.TrimStart());
        Assert.Contains("<html", html);
        Assert.Contains("</html>", html);
    }

    [Fact]
    public void Html_Shows_Pass_Fail_Status()
    {
        var results = new[]
        {
            new ParityResult("pass", []),
            new ParityResult("fail", [new FieldDiff("x", "a", "b", DiffType.ValueMismatch)]),
        };
        var html = HtmlReporter.Generate(results);
        Assert.Contains("PASS", html);
        Assert.Contains("FAIL", html);
    }

    [Fact]
    public void Html_Shows_Diff_Path_And_Values()
    {
        var diffs = new[] { new FieldDiff("sensor.temperature", "22.5", "23.0", DiffType.ValueMismatch) };
        var html = HtmlReporter.Generate([new ParityResult("test", diffs)]);
        Assert.Contains("sensor.temperature", html);
        Assert.Contains("22.5", html);
        Assert.Contains("23.0", html);
    }
}
