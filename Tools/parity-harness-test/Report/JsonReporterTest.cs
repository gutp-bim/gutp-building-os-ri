using System.Text.Json;
using ParityHarness.Core;
using ParityHarness.Report;
using Xunit;

namespace ParityHarness.Test.Report;

public class JsonReporterTest
{
    [Fact]
    public void All_Pass_Report_Is_Valid_Json()
    {
        var results = new[]
        {
            new ParityResult("scenario-a", []),
            new ParityResult("scenario-b", []),
        };
        var json = JsonReporter.Generate(results);
        var doc = JsonDocument.Parse(json);
        Assert.Equal(2, doc.RootElement.GetProperty("totalScenarios").GetInt32());
        Assert.Equal(2, doc.RootElement.GetProperty("passedScenarios").GetInt32());
        Assert.Equal(0, doc.RootElement.GetProperty("failedScenarios").GetInt32());
        Assert.True(doc.RootElement.GetProperty("allPassed").GetBoolean());
    }

    [Fact]
    public void Failed_Scenario_Includes_Diff_Details()
    {
        var diffs = new[] { new FieldDiff("temperature", "22.5", "23", DiffType.ValueMismatch) };
        var results = new[] { new ParityResult("sensor-check", diffs) };
        var json = JsonReporter.Generate(results);
        var doc = JsonDocument.Parse(json);
        var resultArr = doc.RootElement.GetProperty("results");
        Assert.Equal(1, resultArr.GetArrayLength());
        var first = resultArr[0];
        Assert.Equal("sensor-check", first.GetProperty("scenarioName").GetString());
        Assert.False(first.GetProperty("passed").GetBoolean());
        Assert.Equal(1, first.GetProperty("diffCount").GetInt32());
        var diffArr = first.GetProperty("diffs");
        Assert.Equal(1, diffArr.GetArrayLength());
        Assert.Equal("temperature", diffArr[0].GetProperty("path").GetString());
    }

    [Fact]
    public void Report_Contains_GeneratedAt_Timestamp()
    {
        var json = JsonReporter.Generate([new ParityResult("x", [])]);
        var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("generatedAt", out _));
    }
}
