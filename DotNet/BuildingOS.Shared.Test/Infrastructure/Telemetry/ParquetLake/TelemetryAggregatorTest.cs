using BuildingOS.Shared.Infrastructure.Telemetry;
using BuildingOS.Shared.Infrastructure.Telemetry.ParquetLake;

namespace BuildingOS.Shared.Test.Infrastructure.Telemetry.ParquetLake;

public class TelemetryAggregatorTest
{
    private static ValidTelemetryData Row(string isoUtc, double? value) => new()
    {
        PointId = "p1", Building = "b1", DeviceId = "d1", Name = "temp",
        Datetime = isoUtc, Value = value,
    };

    [Fact]
    public void Aggregate_Hourly_BucketsByHour_ComputesAvgMinMaxCount()
    {
        var rows = new[]
        {
            Row("2026-06-12T12:05:00Z", 10),
            Row("2026-06-12T12:55:00Z", 20),  // same hour
            Row("2026-06-12T13:10:00Z", 100), // next hour
        };

        var buckets = TelemetryAggregator.Aggregate(rows, AggregationBucket.Hour);

        Assert.Equal(2, buckets.Count);
        var h12 = buckets[0];
        Assert.Equal(new DateTime(2026, 6, 12, 12, 0, 0, DateTimeKind.Utc), h12.BucketStartUtc);
        Assert.Equal(15, h12.Avg);
        Assert.Equal(10, h12.Min);
        Assert.Equal(20, h12.Max);
        Assert.Equal(2, h12.Count);
        Assert.Equal("p1", h12.PointId); // metadata carried from the bucket's rows
        Assert.Equal(100, buckets[1].Avg);
    }

    [Fact]
    public void Aggregate_Daily_BucketsByUtcDay()
    {
        var rows = new[]
        {
            Row("2026-06-12T23:30:00Z", 4),
            Row("2026-06-13T00:30:00Z", 6), // next UTC day
        };

        var buckets = TelemetryAggregator.Aggregate(rows, AggregationBucket.Day);

        Assert.Equal(2, buckets.Count);
        Assert.Equal(new DateTime(2026, 6, 12, 0, 0, 0, DateTimeKind.Utc), buckets[0].BucketStartUtc);
        Assert.Equal(new DateTime(2026, 6, 13, 0, 0, 0, DateTimeKind.Utc), buckets[1].BucketStartUtc);
    }

    [Fact]
    public void Aggregate_NullValues_CountedButExcludedFromNumericAggregates()
    {
        var rows = new[]
        {
            Row("2026-06-12T12:05:00Z", null),
            Row("2026-06-12T12:15:00Z", 30),
            Row("2026-06-12T12:25:00Z", null),
        };

        var bucket = Assert.Single(TelemetryAggregator.Aggregate(rows, AggregationBucket.Hour));

        Assert.Equal(3, bucket.Count);   // all rows counted
        Assert.Equal(30, bucket.Avg);    // only the non-null value
        Assert.Equal(30, bucket.Min);
        Assert.Equal(30, bucket.Max);
    }

    [Fact]
    public void Aggregate_AllNullValues_NumericAggregatesNull_CountKept()
    {
        var rows = new[] { Row("2026-06-12T12:05:00Z", null), Row("2026-06-12T12:15:00Z", null) };

        var bucket = Assert.Single(TelemetryAggregator.Aggregate(rows, AggregationBucket.Hour));

        Assert.Equal(2, bucket.Count);
        Assert.Null(bucket.Avg);
        Assert.Null(bucket.Min);
        Assert.Null(bucket.Max);
    }

    [Fact]
    public void Aggregate_SkipsUnparseableTimestamps()
    {
        var rows = new[] { Row("not-a-date", 1), Row("2026-06-12T12:05:00Z", 2) };

        var bucket = Assert.Single(TelemetryAggregator.Aggregate(rows, AggregationBucket.Hour));

        Assert.Equal(1, bucket.Count);
        Assert.Equal(2, bucket.Avg);
    }

    [Fact]
    public void Aggregate_Empty_ReturnsEmpty()
    {
        Assert.Empty(TelemetryAggregator.Aggregate(Array.Empty<ValidTelemetryData>(), AggregationBucket.Hour));
    }
}
