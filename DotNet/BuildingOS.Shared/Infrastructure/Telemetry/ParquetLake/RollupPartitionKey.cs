namespace BuildingOS.Shared.Infrastructure.Telemetry.ParquetLake;

/// <summary>Key generation for rollup Parquet objects (#222): <c>agg_hourly/building_id={b}/year={Y}/month={MM}/day={DD}/hour={HH}/agg-{yyyyMMddHH}.parquet</c>.</summary>
public static class RollupPartitionKey
{
    private const string Prefix = "agg_hourly/";

    /// <summary>Full object key for a building's hour rollup.</summary>
    public static string AggKey(string building, DateTime hourUtc)
        => $"{HourPrefix(building, hourUtc)}agg-{hourUtc:yyyyMMddHH}.parquet";

    /// <summary>Directory prefix for a building's hour rollup (for listing).</summary>
    public static string HourPrefix(string building, DateTime hourUtc)
        => $"{Prefix}building_id={building}/year={hourUtc.Year:D4}/month={hourUtc.Month:D2}/day={hourUtc.Day:D2}/hour={hourUtc.Hour:D2}/";

    /// <summary>Month-level listing prefix for a building.</summary>
    public static string MonthPrefix(string building, int year, int month)
        => $"{Prefix}building_id={building}/year={year:D4}/month={month:D2}/";

    public static bool IsRollupKey(string key)
        => key.StartsWith(Prefix, StringComparison.Ordinal) && key.EndsWith(".parquet", StringComparison.Ordinal);

    public static bool TryParseBuilding(string key, out string? building)
    {
        building = null;
        if (!IsRollupKey(key)) return false;
        foreach (var seg in key.Split('/'))
        {
            if (seg.StartsWith("building_id=", StringComparison.Ordinal))
            {
                building = seg["building_id=".Length..];
                return true;
            }
        }
        return false;
    }

    public static bool TryParseHour(string key, out DateTime hourUtc)
    {
        hourUtc = default;
        if (!IsRollupKey(key)) return false;

        // key ends with ...hour={HH}/agg-{yyyyMMddHH}.parquet
        var file = key[(key.LastIndexOf('/') + 1)..];
        if (!file.StartsWith("agg-", StringComparison.Ordinal)) return false;
        var stamp = file["agg-".Length..file.LastIndexOf('.')];
        if (stamp.Length == 10 && DateTime.TryParseExact(stamp, "yyyyMMddHH",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
            out hourUtc))
        {
            return true;
        }
        return false;
    }
}
