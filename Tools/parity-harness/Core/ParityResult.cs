namespace ParityHarness.Core;

public class ParityResult(string scenarioName, IReadOnlyList<FieldDiff> diffs)
{
    public string ScenarioName { get; } = scenarioName;
    public IReadOnlyList<FieldDiff> Diffs { get; } = diffs;
    public bool Passed => Diffs.Count == 0;
    public DateTimeOffset RunAt { get; } = DateTimeOffset.UtcNow;
}

public class ParitySummary
{
    public int Total { get; init; }
    public int Passed { get; init; }
    public int Failed { get; init; }
    public bool AllPassed => Failed == 0;

    public static ParitySummary From(IEnumerable<ParityResult> results)
    {
        var list = results.ToList();
        return new ParitySummary
        {
            Total = list.Count,
            Passed = list.Count(r => r.Passed),
            Failed = list.Count(r => !r.Passed),
        };
    }
}
