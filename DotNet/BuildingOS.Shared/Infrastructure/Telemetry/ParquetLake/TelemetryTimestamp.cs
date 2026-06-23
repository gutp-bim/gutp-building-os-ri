using System.Globalization;

namespace BuildingOS.Shared.Infrastructure.Telemetry.ParquetLake;

/// <summary>
/// Single source of truth for parsing a validated-telemetry <c>datetime</c> string to UTC (#213). The
/// partition hour (<see cref="TelemetryBatchAccumulator"/>) and the <c>time</c> column
/// (<see cref="ParquetTelemetrySerializer"/>) MUST agree, so both go through here. A zone-less timestamp
/// is treated as UTC (the telemetry store is UTC), not local — otherwise the row's partition hour and
/// its time column would diverge on a non-UTC host and the reader would prune it incorrectly.
/// </summary>
internal static class TelemetryTimestamp
{
    public static bool TryParseUtc(string? value, out DateTime utc)
    {
        utc = default;
        if (string.IsNullOrEmpty(value))
        {
            return false;
        }
        if (!DateTime.TryParse(
                value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
        {
            return false;
        }
        // After AdjustToUniversal the kind is Utc; normalise defensively.
        utc = dt.Kind == DateTimeKind.Utc ? dt : dt.ToUniversalTime();
        return true;
    }
}
