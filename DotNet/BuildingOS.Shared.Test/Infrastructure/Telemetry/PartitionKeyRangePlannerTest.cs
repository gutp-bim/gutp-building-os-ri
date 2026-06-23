using BuildingOS.Shared.Infrastructure.Telemetry;

namespace BuildingOS.Shared.Test.Infrastructure.Telemetry;

public class PartitionKeyRangePlannerTest
{
    private static string Key(string b, int y, int mo, int d, int h) =>
        $"building_id={b}/year={y:D4}/month={mo:D2}/day={d:D2}/hour={h:D2}/part-x.parquet";

    [Fact]
    public void MonthPrefixes_OneBuilding_SingleMonth_WithinGrace()
    {
        var start = new DateTime(2026, 6, 12, 10, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2026, 6, 12, 14, 0, 0, DateTimeKind.Utc);

        var prefixes = PartitionKeyRangePlanner.MonthPrefixes(new[] { "bldg-1" }, start, end);

        Assert.Equal(new[] { "building_id=bldg-1/year=2026/month=06/" }, prefixes);
    }

    [Fact]
    public void MonthPrefixes_SpansMonths_AndMultipleBuildings()
    {
        var start = new DateTime(2026, 1, 31, 23, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2026, 3, 1, 1, 0, 0, DateTimeKind.Utc);

        var prefixes = PartitionKeyRangePlanner.MonthPrefixes(new[] { "a", "b" }, start, end);

        // grace pulls start back into Jan, end into Mar → Jan, Feb, Mar × 2 buildings
        Assert.Contains("building_id=a/year=2026/month=01/", prefixes);
        Assert.Contains("building_id=a/year=2026/month=03/", prefixes);
        Assert.Contains("building_id=b/year=2026/month=02/", prefixes);
        Assert.Equal(6, prefixes.Count);
    }

    [Fact]
    public void MonthPrefixes_DedupesBuildings()
    {
        var start = new DateTime(2026, 6, 12, 10, 0, 0, DateTimeKind.Utc);
        var prefixes = PartitionKeyRangePlanner.MonthPrefixes(new[] { "a", "a" }, start, start);
        Assert.Single(prefixes);
    }

    [Theory]
    [InlineData(12, true)]  // same hour as range
    [InlineData(11, true)]  // within grace (1h before start hour)
    [InlineData(15, true)]  // within grace (1h after end hour)
    [InlineData(8, false)]  // outside grace
    [InlineData(18, false)] // outside grace
    public void IsKeyInRange_HourPruning_WithGrace(int keyHour, bool expected)
    {
        var start = new DateTime(2026, 6, 12, 12, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(2026, 6, 12, 14, 0, 0, DateTimeKind.Utc);
        var key = Key("b", 2026, 6, 12, keyHour);

        Assert.Equal(expected, PartitionKeyRangePlanner.IsKeyInRange(key, start, end));
    }

    [Fact]
    public void IsKeyInRange_UnparseableKey_IsConservativelyKept()
    {
        var start = new DateTime(2026, 6, 12, 12, 0, 0, DateTimeKind.Utc);
        Assert.True(PartitionKeyRangePlanner.IsKeyInRange("garbage/no-partition.parquet", start, start));
    }

    [Fact]
    public void ExtractBuildings_ReturnsDistinct()
    {
        var keys = new[]
        {
            Key("bldg-1", 2026, 6, 12, 10),
            Key("bldg-1", 2026, 6, 12, 11),
            Key("bldg-2", 2026, 6, 12, 10),
            "garbage.parquet",
        };

        var buildings = PartitionKeyRangePlanner.ExtractBuildings(keys);

        Assert.Equal(2, buildings.Count);
        Assert.Contains("bldg-1", buildings);
        Assert.Contains("bldg-2", buildings);
    }

    [Fact]
    public void TryParsePartitionStart_ParsesUtcHour()
    {
        Assert.True(PartitionKeyRangePlanner.TryParsePartitionStart(Key("b", 2026, 6, 12, 9), out var ts));
        Assert.Equal(new DateTime(2026, 6, 12, 9, 0, 0, DateTimeKind.Utc), ts);
    }
}
