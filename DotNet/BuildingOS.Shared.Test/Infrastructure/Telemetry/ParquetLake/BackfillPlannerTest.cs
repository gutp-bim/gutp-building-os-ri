using BuildingOS.Shared.Infrastructure.Telemetry.ParquetLake;

namespace BuildingOS.Shared.Test.Infrastructure.Telemetry.ParquetLake;

public class BackfillPlannerTest
{
    [Fact]
    public void HourWindows_ClipsBothEnds_AlignsToHourPartitions()
    {
        var from = new DateTime(2026, 6, 12, 12, 30, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 6, 12, 14, 15, 0, DateTimeKind.Utc);

        var windows = BackfillPlanner.HourWindows(from, to);

        Assert.Equal(3, windows.Count);
        Assert.Equal(new DateTime(2026, 6, 12, 12, 0, 0, DateTimeKind.Utc), windows[0].HourUtc);
        Assert.Equal(from, windows[0].ReadFromUtc);                                  // clipped start
        Assert.Equal(new DateTime(2026, 6, 12, 13, 0, 0, DateTimeKind.Utc), windows[0].ReadToUtc);
        Assert.Equal(new DateTime(2026, 6, 12, 13, 0, 0, DateTimeKind.Utc), windows[1].ReadFromUtc); // full hour
        Assert.Equal(to, windows[2].ReadToUtc);                                      // clipped end
    }

    [Fact]
    public void HourWindows_EmptyWhenToBeforeFrom()
    {
        var t = new DateTime(2026, 6, 12, 12, 0, 0, DateTimeKind.Utc);
        Assert.Empty(BackfillPlanner.HourWindows(t, t));
        Assert.Empty(BackfillPlanner.HourWindows(t.AddHours(1), t));
    }

    [Fact]
    public void BackfillKey_MatchesLakeLayout_AndIsDeterministic()
    {
        var hour = new DateTime(2026, 6, 12, 9, 0, 0, DateTimeKind.Utc);
        var key = BackfillPlanner.BackfillKey("b1", hour);

        Assert.Equal("building_id=b1/year=2026/month=06/day=12/hour=09/part-backfill-2026061209.parquet", key);
        Assert.Equal(key, BackfillPlanner.BackfillKey("b1", hour)); // deterministic → idempotent overwrite
    }
}
