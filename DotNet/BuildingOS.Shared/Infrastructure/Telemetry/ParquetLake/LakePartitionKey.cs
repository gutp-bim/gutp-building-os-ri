namespace BuildingOS.Shared.Infrastructure.Telemetry.ParquetLake;

/// <summary>
/// Builds object keys for the Parquet lake (#213), identical to the layout read by
/// <see cref="PartitionKeyRangePlanner"/> / <see cref="MinioParquetColdTelemetryStore"/>:
/// <c>building_id={b}/year={Y}/month={MM}/day={DD}/hour={HH}/part-{firstSeq}-{lastSeq}.parquet</c>.
/// The <c>firstSeq-lastSeq</c> suffix is the JetStream sequence range of the messages that produced
/// the partition, so re-delivering the same messages writes the same key (idempotent overwrite).
/// </summary>
public static class LakePartitionKey
{
    public static string For(string building, DateTime hourUtc, ulong firstSeq, ulong lastSeq)
        => HourPrefix(building, hourUtc) + $"part-{firstSeq}-{lastSeq}.parquet";

    /// <summary>The hour-partition directory prefix (everything up to and including the trailing slash).</summary>
    public static string HourPrefix(string building, DateTime hourUtc)
    {
        var h = hourUtc.ToUniversalTime();
        return $"building_id={building}/year={h.Year:D4}/month={h.Month:D2}/day={h.Day:D2}/hour={h.Hour:D2}/";
    }
}
