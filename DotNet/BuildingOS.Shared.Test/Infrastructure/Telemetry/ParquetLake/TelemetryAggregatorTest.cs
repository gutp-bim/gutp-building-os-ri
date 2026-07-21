using BuildingOS.Shared.Infrastructure.Telemetry;
using BuildingOS.Shared.Infrastructure.Telemetry.ParquetLake;

namespace BuildingOS.Shared.Test.Infrastructure.Telemetry.ParquetLake;

public class TelemetryAggregatorTest
{
    private static ValidTelemetryData Row(string isoUtc, double? value) => new()
    {
        PointId = "p1", Building = "b1", DeviceId = "d1", Name = "temp",
        Datetime = isoUtc, Value = value, ValueType = value is null ? null : "number",
    };

    private static ValidTelemetryData StringRow(string isoUtc, string text) => new()
    {
        PointId = "p1", Building = "b1", DeviceId = "d1", Name = "mode",
        Datetime = isoUtc, ValueType = "string", ValueText = text,
    };

    private static ValidTelemetryData BoolRow(string isoUtc, bool b) => new()
    {
        PointId = "p1", Building = "b1", DeviceId = "d1", Name = "run",
        Datetime = isoUtc, ValueType = "boolean", ValueBool = b,
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

    [Fact]
    public void Aggregate_NumericBucket_CarriesNumberValueType()
    {
        // #152 Phase B: numeric buckets keep avg/min/max and are tagged "number".
        var bucket = Assert.Single(TelemetryAggregator.Aggregate(
            new[] { Row("2026-06-12T12:05:00Z", 10) }, AggregationBucket.Hour));
        Assert.Equal("number", bucket.ValueType);
        Assert.Null(bucket.LastText);
        Assert.Null(bucket.LastBool);
        Assert.Equal(10, bucket.Avg);
    }

    [Fact]
    public void Aggregate_StringBucket_LastInBucket_IsRepresentative()
    {
        // #152 Phase B, D3=last-in-bucket: the bucket's representative non-numeric value is the latest
        // (by timestamp) in the bucket, regardless of input order. Numeric aggregates stay null.
        var rows = new[]
        {
            StringRow("2026-06-12T12:55:00Z", "off"),   // latest — out of order
            StringRow("2026-06-12T12:05:00Z", "auto"),
        };
        var bucket = Assert.Single(TelemetryAggregator.Aggregate(rows, AggregationBucket.Hour));

        Assert.Equal("string", bucket.ValueType);
        Assert.Equal("off", bucket.LastText);
        Assert.Null(bucket.LastBool);
        Assert.Null(bucket.Avg);
        Assert.Null(bucket.Min);
        Assert.Null(bucket.Max);
        Assert.Equal(2, bucket.Count);
    }

    [Fact]
    public void Aggregate_EqualTimestamps_TieBrokenDeterministicallyById()
    {
        // Two distinct readings share the exact same timestamp → the Id tiebreaker (ordinal) makes the
        // representative order-independent: the greater Id wins regardless of input order.
        const string ts = "2026-06-12T12:00:00Z";
        var a = new ValidTelemetryData { PointId = "p1", Datetime = ts, ValueType = "string", ValueText = "auto", Id = "id-a" };
        var b = new ValidTelemetryData { PointId = "p1", Datetime = ts, ValueType = "string", ValueText = "off", Id = "id-b" };

        var forward = Assert.Single(TelemetryAggregator.Aggregate(new[] { a, b }, AggregationBucket.Hour));
        var reverse = Assert.Single(TelemetryAggregator.Aggregate(new[] { b, a }, AggregationBucket.Hour));

        Assert.Equal("off", forward.LastText);   // id-b > id-a
        Assert.Equal("off", reverse.LastText);    // same result regardless of order
    }

    [Fact]
    public void Aggregate_BooleanBucket_LastInBucket_IsRepresentative()
    {
        var rows = new[]
        {
            BoolRow("2026-06-12T12:05:00Z", false),
            BoolRow("2026-06-12T12:50:00Z", true),  // latest
        };
        var bucket = Assert.Single(TelemetryAggregator.Aggregate(rows, AggregationBucket.Hour));

        Assert.Equal("boolean", bucket.ValueType);
        Assert.True(bucket.LastBool);
        Assert.Null(bucket.LastText);
        Assert.Null(bucket.Avg);
    }
}
