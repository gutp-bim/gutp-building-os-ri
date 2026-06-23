using System.Text.Json;
using System.Text.Json.Serialization;
using ParityHarness.Core;

namespace ParityHarness.Report;

public static class JsonReporter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static string Generate(IEnumerable<ParityResult> results)
    {
        var list = results.ToList();
        var summary = ParitySummary.From(list);
        var report = new
        {
            GeneratedAt = DateTimeOffset.UtcNow,
            TotalScenarios = summary.Total,
            PassedScenarios = summary.Passed,
            FailedScenarios = summary.Failed,
            AllPassed = summary.AllPassed,
            Results = list.Select(r => new
            {
                r.ScenarioName,
                r.Passed,
                DiffCount = r.Diffs.Count,
                Diffs = r.Diffs.Select(d => new
                {
                    d.Path,
                    d.Expected,
                    d.Actual,
                    Type = d.Type.ToString(),
                }),
            }),
        };
        return JsonSerializer.Serialize(report, Options);
    }
}
