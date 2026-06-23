using BuildingOS.Shared.Infrastructure.Telemetry.ParquetLake;

namespace BuildingOS.Shared.Test.Infrastructure.Telemetry.ParquetLake;

/// <summary>
/// Regression: the NATS pull-consumer derives idle-heartbeat ≈ Expires/2 and rejects ≥ 30s. The fetch
/// poll window must therefore stay ≤ ~20s regardless of the (possibly multi-minute) flush interval,
/// otherwise every fetch throws "idleHeartbeat must be less than 00:00:30" and the lake writer never
/// persists anything (the default 5-minute flush interval triggered exactly this).
/// </summary>
public class ParquetLakeWriterFetchExpiresTest
{
    [Theory]
    [InlineData(300)]  // default 5 min — the broken case
    [InlineData(60)]   // 1 min
    [InlineData(3600)] // 1 hour
    public void LongFlushInterval_ClampsPollWindow_KeepingIdleHeartbeatUnder30s(int flushSeconds)
    {
        var expires = ParquetLakeWriterWorker.ComputeFetchExpires(TimeSpan.FromSeconds(flushSeconds));

        Assert.True(expires <= TimeSpan.FromSeconds(20), $"expires {expires} must be ≤ 20s");
        // idle-heartbeat ≈ expires/2 must be strictly < 30s.
        Assert.True(expires.TotalSeconds / 2 < 30);
    }

    [Theory]
    [InlineData(5, 5)]    // short interval passes through
    [InlineData(20, 20)]  // exactly the cap
    [InlineData(1, 1)]    // floor
    public void ShortFlushInterval_IsUsedDirectly(int flushSeconds, int expectedSeconds)
    {
        var expires = ParquetLakeWriterWorker.ComputeFetchExpires(TimeSpan.FromSeconds(flushSeconds));
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), expires);
    }

    [Fact]
    public void SubSecondInterval_FloorsToOneSecond()
    {
        var expires = ParquetLakeWriterWorker.ComputeFetchExpires(TimeSpan.FromMilliseconds(100));
        Assert.Equal(TimeSpan.FromSeconds(1), expires);
    }
}
