using BuildingOS.Shared.Infrastructure.Telemetry.ParquetLake;

namespace BuildingOS.Shared.Test.Infrastructure.Telemetry.ParquetLake;

/// <summary>Unit tests for RollupPartitionKey, RollupAggregator, and RollupSerializer (#222).</summary>
public class RollupTest
{
    // ── RollupPartitionKey ──────────────────────────────────────────────────

    [Fact]
    public void AggKey_Format_IsCorrect()
    {
        var hour = new DateTime(2026, 6, 13, 14, 0, 0, DateTimeKind.Utc);
        var key = RollupPartitionKey.AggKey("building-A", hour);
        Assert.Equal(
            "agg_hourly/building_id=building-A/year=2026/month=06/day=13/hour=14/agg-2026061314.parquet",
            key);
    }

    [Fact]
    public void AggKey_HourPrefix_IsCorrect()
    {
        var hour = new DateTime(2026, 1, 5, 8, 0, 0, DateTimeKind.Utc);
        var prefix = RollupPartitionKey.HourPrefix("bldg-1", hour);
        Assert.Equal("agg_hourly/building_id=bldg-1/year=2026/month=01/day=05/hour=08/", prefix);
    }

    [Fact]
    public void TryParseBuilding_ReturnsBuilding_WhenKeyValid()
    {
        var key = "agg_hourly/building_id=my-bldg/year=2026/month=06/day=01/hour=10/agg-2026060110.parquet";
        Assert.True(RollupPartitionKey.TryParseBuilding(key, out var b));
        Assert.Equal("my-bldg", b);
    }

    [Fact]
    public void TryParseBuilding_ReturnsFalse_ForNonRollupKey()
    {
        var key = "building_id=x/year=2026/month=06/day=01/hour=10/part-1-2.parquet";
        Assert.False(RollupPartitionKey.TryParseBuilding(key, out _));
    }

    [Fact]
    public void TryParseHour_RoundTrips()
    {
        var hour = new DateTime(2026, 3, 22, 9, 0, 0, DateTimeKind.Utc);
        var key = RollupPartitionKey.AggKey("b", hour);
        Assert.True(RollupPartitionKey.TryParseHour(key, out var parsed));
        Assert.Equal(hour, parsed);
    }

    // ── RollupAggregator ────────────────────────────────────────────────────

    private static ValidTelemetryData Row(string pid, double? v, string datetime, string building = "B", string device = "D", string name = "temp") =>
        new() { PointId = pid, Value = v, Datetime = datetime, Building = building, DeviceId = device, Name = name, Id = Guid.NewGuid().ToString() };

    [Fact]
    public void Compute_SinglePoint_AvgMinMaxCount()
    {
        var rows = new[]
        {
            Row("p1", 10.0, "2026-06-13T14:00:00Z"),
            Row("p1", 20.0, "2026-06-13T14:30:00Z"),
            Row("p1", 30.0, "2026-06-13T14:45:00Z"),
        };
        var rollup = RollupAggregator.Compute(rows);
        Assert.Single(rollup);
        var r = rollup[0];
        Assert.Equal("p1", r.PointId);
        Assert.Equal(20.0, r.Avg!.Value, precision: 10);
        Assert.Equal(10.0, r.MinValue!.Value, precision: 10);
        Assert.Equal(30.0, r.MaxValue!.Value, precision: 10);
        Assert.Equal(3, r.Count);
    }

    [Fact]
    public void Compute_MultiplePoints_IndependentRows()
    {
        var rows = new[]
        {
            Row("p1", 10.0, "2026-06-13T14:00:00Z"),
            Row("p2", 100.0, "2026-06-13T14:15:00Z"),
            Row("p2", 200.0, "2026-06-13T14:30:00Z"),
        };
        var rollup = RollupAggregator.Compute(rows).ToDictionary(r => r.PointId!);
        Assert.Equal(2, rollup.Count);
        Assert.Equal(10.0, rollup["p1"].Avg!.Value, precision: 10);
        Assert.Equal(150.0, rollup["p2"].Avg!.Value, precision: 10);
        Assert.Equal(2, rollup["p2"].Count);
    }

    [Fact]
    public void Compute_NullValues_CountIncludedAvgExcluded()
    {
        var rows = new[]
        {
            Row("p1", null, "2026-06-13T14:00:00Z"),
            Row("p1", 10.0, "2026-06-13T14:30:00Z"),
        };
        var rollup = RollupAggregator.Compute(rows);
        Assert.Single(rollup);
        var r = rollup[0];
        Assert.Equal(10.0, r.Avg!.Value, precision: 10);
        Assert.Equal(2, r.Count); // null rows counted
    }

    [Fact]
    public void Compute_AllNullValues_AvgMinMaxNull()
    {
        var rows = new[] { Row("p1", null, "2026-06-13T14:00:00Z") };
        var rollup = RollupAggregator.Compute(rows);
        Assert.Single(rollup);
        var r = rollup[0];
        Assert.Null(r.Avg);
        Assert.Null(r.MinValue);
        Assert.Null(r.MaxValue);
        Assert.Equal(1, r.Count);
    }

    [Fact]
    public void Compute_HourUtc_IsTruncatedToHour()
    {
        var hour = new DateTime(2026, 6, 13, 14, 0, 0, DateTimeKind.Utc);
        var rows = new[] { Row("p1", 1.0, "2026-06-13T14:42:00Z") };
        var rollup = RollupAggregator.Compute(rows, hour);
        Assert.Equal(hour, rollup[0].HourUtc);
    }

    [Fact]
    public void Compute_EmptyRows_ReturnsEmpty()
    {
        var rollup = RollupAggregator.Compute(Array.Empty<ValidTelemetryData>());
        Assert.Empty(rollup);
    }

    // ── RollupSerializer ────────────────────────────────────────────────────

    [Fact]
    public async Task WriteRead_RoundTrips()
    {
        var hour = new DateTime(2026, 6, 13, 8, 0, 0, DateTimeKind.Utc);
        var rows = new[]
        {
            new RollupRow("p1", "B", "D", "temp", 15.5, 10.0, 21.0, 4, hour),
            new RollupRow("p2", "B", "D2", "humi", null, null, null, 2, hour),
        };

        using var ms = new MemoryStream();
        await RollupSerializer.WriteAsync(rows, ms);
        ms.Position = 0;

        var read = await RollupSerializer.ReadAsync(ms);
        Assert.Equal(2, read.Count);

        var p1 = read.Single(r => r.PointId == "p1");
        Assert.Equal(15.5, p1.Avg!.Value, precision: 10);
        Assert.Equal(10.0, p1.MinValue!.Value, precision: 10);
        Assert.Equal(21.0, p1.MaxValue!.Value, precision: 10);
        Assert.Equal(4, p1.Count);
        Assert.Equal(hour, p1.HourUtc);

        var p2 = read.Single(r => r.PointId == "p2");
        Assert.Null(p2.Avg);
        Assert.Equal(2, p2.Count);
    }

    [Fact]
    public async Task WriteRead_EmptyRows_RoundTrips()
    {
        using var ms = new MemoryStream();
        await RollupSerializer.WriteAsync(Array.Empty<RollupRow>(), ms);
        ms.Position = 0;
        var read = await RollupSerializer.ReadAsync(ms);
        Assert.Empty(read);
    }
}
