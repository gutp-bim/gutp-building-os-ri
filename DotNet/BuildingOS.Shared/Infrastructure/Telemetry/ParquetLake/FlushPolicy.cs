namespace BuildingOS.Shared.Infrastructure.Telemetry.ParquetLake;

/// <summary>
/// Pure flush-trigger policy for the Parquet lake writer (#213): flush when enough rows have buffered
/// or the flush interval has elapsed (and there is something to flush).
/// </summary>
public static class FlushPolicy
{
    public static bool ShouldFlush(int bufferedRows, int maxRows, TimeSpan elapsed, TimeSpan interval)
    {
        if (bufferedRows <= 0)
        {
            return false;
        }
        return bufferedRows >= maxRows || elapsed >= interval;
    }
}
