using BuildingOS.Shared.Infrastructure.Telemetry.ParquetLake;

namespace BuildingOS.Shared.Test.Infrastructure.Telemetry.ParquetLake;

/// <summary>
/// Regression: Parquet.Net decodes the <c>time</c> column as DateTime with Kind=Unspecified. The reader
/// must treat that as the UTC it was written as — calling ToUniversalTime() would shift by the host
/// offset, so on a non-UTC host (e.g. JST +9) every row falls outside the UTC query window and warm /
/// range / query reads return empty.
/// </summary>
public class ParquetLakeScanTimeTest
{
    [Fact]
    public void Unspecified_IsTreatedAsUtc_NotShiftedByHostOffset()
    {
        var unspecified = new DateTime(2026, 6, 14, 21, 34, 0, DateTimeKind.Unspecified);

        var normalized = ParquetLakeScan.NormalizeUtc(unspecified);

        Assert.NotNull(normalized);
        Assert.Equal(DateTimeKind.Utc, normalized!.Value.Kind);
        // Same wall-clock value, just tagged UTC — no host-offset shift.
        Assert.Equal(new DateTime(2026, 6, 14, 21, 34, 0, DateTimeKind.Utc), normalized.Value);
    }

    [Fact]
    public void DateTimeOffset_UsesUtcDateTime()
    {
        var dto = new DateTimeOffset(2026, 6, 14, 21, 34, 0, TimeSpan.FromHours(9)); // 12:34 UTC
        var normalized = ParquetLakeScan.NormalizeUtc(dto);
        Assert.Equal(new DateTime(2026, 6, 14, 12, 34, 0, DateTimeKind.Utc), normalized);
    }

    [Fact]
    public void UtcDateTime_PassesThrough()
    {
        var utc = new DateTime(2026, 6, 14, 21, 34, 0, DateTimeKind.Utc);
        Assert.Equal(utc, ParquetLakeScan.NormalizeUtc(utc));
    }

    [Fact]
    public void NonTimestamp_ReturnsNull()
    {
        Assert.Null(ParquetLakeScan.NormalizeUtc(null));
        Assert.Null(ParquetLakeScan.NormalizeUtc("not-a-time"));
    }
}
