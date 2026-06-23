namespace BuildingOS.Shared.Infrastructure.Telemetry.ParquetLake;

/// <summary>Pure tail-merge gate (#220): determines whether a warm query should invoke the JetStream tail reader to fill the flush-interval gap.</summary>
public static class TailMergePolicy
{
    /// <summary>
    /// Returns true when <paramref name="end"/> falls within the lookback window of <paramref name="now"/>
    /// (i.e., the query's end time is recent enough that unflushed rows may exist in JetStream).
    /// A non-positive <paramref name="lookbackSec"/> disables tail merge entirely.
    /// </summary>
    public static bool ShouldMergeTail(DateTime end, DateTime now, int lookbackSec)
        => lookbackSec > 0 && end >= now.AddSeconds(-lookbackSec);
}
