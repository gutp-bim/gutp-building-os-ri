using BuildingOS.Shared.Infrastructure.Telemetry.ParquetLake;

namespace BuildingOS.Shared.Test.Infrastructure.Telemetry.ParquetLake;

public class CompactionPlannerTest
{
    private static string Part(string b, int hour, int first, int last) =>
        $"building_id={b}/year=2026/month=06/day=12/hour={hour:D2}/part-{first}-{last}.parquet";
    private static string Compact(string b, int hour) =>
        $"building_id={b}/year=2026/month=06/day=12/hour={hour:D2}/compact-2026061{2:D1}{hour:D2}.parquet";

    private static readonly DateTime Now = new(2026, 6, 12, 18, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Grace = TimeSpan.FromMinutes(30);

    [Fact]
    public void IsHourSettled_TrueOnlyAfterHourEndPlusGrace()
    {
        var hour = new DateTime(2026, 6, 12, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(CompactionPlanner.IsHourSettled(hour, hour.AddHours(1).AddMinutes(30), Grace)); // exactly at boundary
        Assert.True(CompactionPlanner.IsHourSettled(hour, hour.AddHours(2), Grace));
        Assert.False(CompactionPlanner.IsHourSettled(hour, hour.AddHours(1).AddMinutes(15), Grace)); // within grace
        Assert.False(CompactionPlanner.IsHourSettled(hour, hour.AddMinutes(30), Grace)); // hour not over
    }

    [Fact]
    public void Plan_SelectsSettledHourWithMultipleParts()
    {
        var keys = new[] { Part("b1", 12, 1, 5), Part("b1", 12, 6, 10) };

        var target = Assert.Single(CompactionPlanner.Plan(keys, Now, Grace));

        Assert.Equal("b1", target.Building);
        Assert.Equal(new DateTime(2026, 6, 12, 12, 0, 0, DateTimeKind.Utc), target.HourUtc);
        Assert.Equal(2, target.SourceKeys.Count);
        Assert.Equal("building_id=b1/year=2026/month=06/day=12/hour=12/compact-2026061212.parquet", target.CompactKey);
    }

    [Fact]
    public void Plan_SkipsUnsettledHour()
    {
        // hour 17 ends at 18:00; with now=18:00 and 30m grace it is NOT yet settled.
        var keys = new[] { Part("b1", 17, 1, 5), Part("b1", 17, 6, 10) };
        Assert.Empty(CompactionPlanner.Plan(keys, Now, Grace));
    }

    [Fact]
    public void Plan_SkipsSinglePart()
    {
        var keys = new[] { Part("b1", 12, 1, 5) };
        Assert.Empty(CompactionPlanner.Plan(keys, Now, Grace));
    }

    [Fact]
    public void Plan_IncludesExistingCompact_WhenNewPartsArrived()
    {
        // a compact already exists but 2 new parts landed after it → recompact, merging all three.
        var keys = new[] { Compact("b1", 12), Part("b1", 12, 11, 15), Part("b1", 12, 16, 20) };

        var target = Assert.Single(CompactionPlanner.Plan(keys, Now, Grace));

        Assert.Equal(3, target.SourceKeys.Count);
        Assert.Contains(Compact("b1", 12), target.SourceKeys);
    }

    [Fact]
    public void Plan_SkipsHourWithOnlyCompact_NoParts()
    {
        var keys = new[] { Compact("b1", 12) }; // already compacted, nothing new
        Assert.Empty(CompactionPlanner.Plan(keys, Now, Grace));
    }

    [Fact]
    public void Plan_RecompactsCompactPlusSingleLeftoverPart()
    {
        // A compact exists and one part remains (e.g. an interrupted delete) → fold it back in.
        var keys = new[] { Compact("b1", 12), Part("b1", 12, 11, 15) };

        var target = Assert.Single(CompactionPlanner.Plan(keys, Now, Grace));

        Assert.Equal(2, target.SourceKeys.Count);
        Assert.Contains(Compact("b1", 12), target.SourceKeys);
    }

    [Fact]
    public void Plan_SeparatesBuildingsAndHours()
    {
        var keys = new[]
        {
            Part("b1", 12, 1, 5), Part("b1", 12, 6, 10),
            Part("b2", 12, 1, 5), Part("b2", 12, 6, 10),
            Part("b1", 13, 1, 5), Part("b1", 13, 6, 10),
        };
        var targets = CompactionPlanner.Plan(keys, Now, Grace);
        Assert.Equal(3, targets.Count); // (b1,12) (b2,12) (b1,13)
    }
}
