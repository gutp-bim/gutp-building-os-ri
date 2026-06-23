using BuildingOS.Shared.Infrastructure.Telemetry;
using BuildingOS.Shared.Infrastructure.Telemetry.ParquetLake;

namespace BuildingOS.Shared.Test.Infrastructure.Telemetry.ParquetLake;

public class ParquetLakeReadPlannerTest
{
    private const string Dir = "building_id=b1/year=2026/month=06/day=12/hour=12/";
    private const string Dir2 = "building_id=b1/year=2026/month=06/day=12/hour=13/";

    [Fact]
    public void SelectObjectKeys_PrefersCompact_WhenPresentInHour()
    {
        var keys = new[]
        {
            Dir + "part-1-10.parquet",
            Dir + "part-11-20.parquet",
            Dir + "compact-2026061212.parquet", // wins for this hour
            Dir2 + "part-30-40.parquet",        // no compact here → kept
        };

        var selected = ParquetLakeReadPlanner.SelectObjectKeys(keys);

        Assert.Contains(Dir + "compact-2026061212.parquet", selected);
        Assert.DoesNotContain(Dir + "part-1-10.parquet", selected);
        Assert.DoesNotContain(Dir + "part-11-20.parquet", selected);
        Assert.Contains(Dir2 + "part-30-40.parquet", selected);
    }

    [Fact]
    public void SelectObjectKeys_KeepsAllParts_WhenNoCompact()
    {
        var keys = new[] { Dir + "part-1-10.parquet", Dir + "part-11-20.parquet" };
        var selected = ParquetLakeReadPlanner.SelectObjectKeys(keys);
        Assert.Equal(2, selected.Count);
    }

    [Fact]
    public void DedupById_LastWins_KeepsNullId_SortsByTime()
    {
        var rows = new[]
        {
            new ValidTelemetryData { Id = "a", Datetime = "2026-06-12T12:30:00.0000000Z", Value = 1 },
            new ValidTelemetryData { Id = "a", Datetime = "2026-06-12T12:30:00.0000000Z", Value = 9 }, // dup → last wins
            new ValidTelemetryData { Id = null, Datetime = "2026-06-12T12:10:00.0000000Z", Value = 2 },
            new ValidTelemetryData { Id = "b", Datetime = "2026-06-12T12:20:00.0000000Z", Value = 3 },
        };

        var result = ParquetLakeReadPlanner.DedupById(rows);

        Assert.Equal(3, result.Length); // a(dedup) + null + b
        // DedupById returns rows sorted ascending by time: null@12:10, b@12:20, a@12:30 (last-wins value 9)
        Assert.Equal(new double?[] { 2d, 3d, 9d }, result.Select(r => r.Value));
        Assert.Equal("2026-06-12T12:10:00.0000000Z", result[0].Datetime);
    }

    [Fact]
    public void LookbackHours_DescendingFromNowHour_Inclusive()
    {
        var now = new DateTime(2026, 6, 12, 12, 34, 0, DateTimeKind.Utc);
        var hours = ParquetLakeReadPlanner.LookbackHours(now, 3);

        // newest first: 12, 11, 10 (3 hours back, the current hour + 2 prior)
        Assert.Equal(3, hours.Count);
        Assert.Equal(new DateTime(2026, 6, 12, 12, 0, 0, DateTimeKind.Utc), hours[0]);
        Assert.Equal(new DateTime(2026, 6, 12, 11, 0, 0, DateTimeKind.Utc), hours[1]);
        Assert.Equal(new DateTime(2026, 6, 12, 10, 0, 0, DateTimeKind.Utc), hours[2]);
    }

    [Fact]
    public void HourPrefix_MatchesLakeLayout()
    {
        var prefix = LakePartitionKey.HourPrefix("b1", new DateTime(2026, 6, 12, 12, 0, 0, DateTimeKind.Utc));
        Assert.Equal("building_id=b1/year=2026/month=06/day=12/hour=12/", prefix);
    }
}
