using BuildingOS.Shared.Infrastructure.Telemetry.ParquetLake;

namespace BuildingOS.Shared.Test.Infrastructure.Telemetry.ParquetLake;

public class TelemetryBatchAccumulatorTest
{
    private static ValidTelemetryData Row(string? id, string? building, string isoTime, double value) => new()
    {
        Id = id, Building = building, Datetime = isoTime, Value = value, PointId = "p1",
    };

    [Fact]
    public void Add_GroupsByBuildingAndHour()
    {
        var acc = new TelemetryBatchAccumulator();
        acc.Add(1, new[]
        {
            Row("a", "b1", "2026-06-12T12:10:00Z", 1),
            Row("b", "b1", "2026-06-12T12:50:00Z", 2), // same building+hour
            Row("c", "b1", "2026-06-12T13:05:00Z", 3), // next hour
            Row("d", "b2", "2026-06-12T12:30:00Z", 4), // other building
        });

        var batches = acc.Drain();

        Assert.Equal(3, batches.Count); // (b1,12) (b1,13) (b2,12)
        var b1h12 = batches.Single(b => b.Building == "b1" && b.HourUtc.Hour == 12);
        Assert.Equal(2, b1h12.Rows.Count);
    }

    [Fact]
    public void Add_DedupesById_LastWins()
    {
        var acc = new TelemetryBatchAccumulator();
        acc.Add(1, new[] { Row("dup", "b1", "2026-06-12T12:10:00Z", 1) });
        acc.Add(2, new[] { Row("dup", "b1", "2026-06-12T12:20:00Z", 9) });

        var batch = Assert.Single(acc.Drain());
        var row = Assert.Single(batch.Rows);
        Assert.Equal(9, row.Value); // last wins
    }

    [Fact]
    public void Add_KeepsRowsWithoutId()
    {
        var acc = new TelemetryBatchAccumulator();
        acc.Add(1, new[]
        {
            Row(null, "b1", "2026-06-12T12:10:00Z", 1),
            Row(null, "b1", "2026-06-12T12:11:00Z", 2),
        });
        var batch = Assert.Single(acc.Drain());
        Assert.Equal(2, batch.Rows.Count);
    }

    [Fact]
    public void Add_TracksSeqRangePerPartition()
    {
        var acc = new TelemetryBatchAccumulator();
        acc.Add(5, new[] { Row("a", "b1", "2026-06-12T12:10:00Z", 1) });
        acc.Add(9, new[] { Row("b", "b1", "2026-06-12T12:20:00Z", 2) });
        acc.Add(3, new[] { Row("c", "b1", "2026-06-12T12:30:00Z", 3) });

        var batch = Assert.Single(acc.Drain());
        Assert.Equal(3UL, batch.FirstSeq);
        Assert.Equal(9UL, batch.LastSeq);
    }

    [Fact]
    public void Add_TracksMaxEventTime_NotHourCeiling()
    {
        var acc = new TelemetryBatchAccumulator();
        acc.Add(1, new[]
        {
            Row("a", "b1", "2026-06-12T12:10:00Z", 1),
            Row("b", "b1", "2026-06-12T12:50:00Z", 2), // newest in the hour
            Row("c", "b1", "2026-06-12T12:30:00Z", 3),
        });

        var batch = Assert.Single(acc.Drain());
        // The freshness metric uses this; it must be the actual newest event (12:50), not 13:00.
        Assert.Equal(new DateTime(2026, 6, 12, 12, 50, 0, DateTimeKind.Utc), batch.MaxEventUtc);
    }

    [Fact]
    public void Add_SkipsAndCountsUnparseableTimestamps()
    {
        var acc = new TelemetryBatchAccumulator();
        var accepted = acc.Add(1, new[]
        {
            Row("a", "b1", "2026-06-12T12:10:00Z", 1),
            Row("b", "b1", "not-a-date", 2),
        });

        Assert.Equal(1, accepted);
        Assert.Equal(1, acc.SkippedNoTimestamp);
        Assert.Equal(1, acc.RowCount);
    }

    [Fact]
    public void Add_NullBuilding_BucketsAsUnknown()
    {
        var acc = new TelemetryBatchAccumulator();
        acc.Add(1, new[] { Row("a", null, "2026-06-12T12:10:00Z", 1) });
        var batch = Assert.Single(acc.Drain());
        Assert.Equal("unknown", batch.Building);
    }

    [Fact]
    public void Drain_ClearsState()
    {
        var acc = new TelemetryBatchAccumulator();
        acc.Add(1, new[] { Row("a", "b1", "2026-06-12T12:10:00Z", 1) });
        acc.Drain();
        Assert.True(acc.IsEmpty);
        Assert.Equal(0, acc.RowCount);
        Assert.Equal(0, acc.SkippedNoTimestamp);
    }
}
