using ParityHarness.Core;
using Xunit;

namespace ParityHarness.Test.Core;

public class ParityResultTest
{
    [Fact]
    public void No_Diffs_Is_Passed()
    {
        var result = new ParityResult("scenario-a", []);
        Assert.True(result.Passed);
    }

    [Fact]
    public void With_Diffs_Is_Failed()
    {
        var diffs = new[] { new FieldDiff("x", "1", "2", DiffType.ValueMismatch) };
        var result = new ParityResult("scenario-b", diffs);
        Assert.False(result.Passed);
    }

    [Fact]
    public void Summary_Counts_Are_Correct()
    {
        var results = new[]
        {
            new ParityResult("pass-1", []),
            new ParityResult("pass-2", []),
            new ParityResult("fail-1", [new FieldDiff("x", "1", "2", DiffType.ValueMismatch)]),
        };
        var summary = ParitySummary.From(results);
        Assert.Equal(3, summary.Total);
        Assert.Equal(2, summary.Passed);
        Assert.Equal(1, summary.Failed);
        Assert.False(summary.AllPassed);
    }

    [Fact]
    public void All_Passed_Summary_Is_True()
    {
        var results = new[]
        {
            new ParityResult("pass-1", []),
            new ParityResult("pass-2", []),
        };
        var summary = ParitySummary.From(results);
        Assert.True(summary.AllPassed);
    }
}
