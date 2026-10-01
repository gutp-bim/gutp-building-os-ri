using BuildingOs.ApiServer.Telemetry;

namespace BuildingOS.ApiServer.Test.Telemetry;

/// <summary>
/// #551: per-15-minute receive counts over 24 h, counted on the server so a 5-second point's ~17k
/// readings never cross the wire. Buckets match the web client's bar exactly: oldest first, aligned to
/// the window end (not the wall clock), half-open [start, end).
/// </summary>
public class TelemetryCoverageTest
{
    private static readonly DateTimeOffset End = new(2026, 10, 2, 12, 7, 30, TimeSpan.Zero);

    [Fact]
    public void Window_Is96BucketsOf15Minutes_EndingAtTheRequestedEnd()
    {
        var c = TelemetryCoverage.Count([], End);

        Assert.Equal(96, c.Counts.Length);
        Assert.Equal(900, c.BucketSeconds);
        Assert.Equal(End.AddHours(-24), c.WindowStart);
        Assert.Equal(End, c.WindowEnd);
        Assert.All(c.Counts, n => Assert.Equal(0, n));
    }

    [Fact]
    public void CountsFallIntoHalfOpenBuckets()
    {
        var start = End.AddHours(-24);
        var c = TelemetryCoverage.Count(
        [
            start.ToString("O"),                       // first instant → bucket 0
            start.AddMinutes(14).ToString("O"),         // still bucket 0
            start.AddMinutes(15).ToString("O"),         // boundary → bucket 1
            End.AddSeconds(-1).ToString("O"),           // last bucket
        ], End);

        Assert.Equal(2, c.Counts[0]);
        Assert.Equal(1, c.Counts[1]);
        Assert.Equal(1, c.Counts[95]);
        Assert.Equal(4, c.Counts.Sum());
    }

    [Fact]
    public void ReadingsOutsideTheWindow_OrUnparsable_AreDropped()
    {
        var c = TelemetryCoverage.Count(
        [
            End.AddHours(-24).AddSeconds(-1).ToString("O"), // just before
            End.ToString("O"),                              // end is exclusive
            "not a date",
            null,
            End.AddMinutes(-1).ToString("O"),
        ], End);

        Assert.Equal(1, c.Counts.Sum());
    }

    [Fact]
    public void OffsetTimestamps_AreCountedByTheirInstant()
    {
        // 21:00+09:00 is 12:00Z, which is inside the last bucket ([11:52:30Z, 12:07:30Z)).
        var c = TelemetryCoverage.Count(["2026-10-02T21:00:00+09:00"], End);

        Assert.Equal(1, c.Counts[95]);
    }
}
