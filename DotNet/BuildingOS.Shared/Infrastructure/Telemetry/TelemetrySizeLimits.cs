using System.Globalization;
using System.Text;
using System.Text.Json;

namespace BuildingOS.Shared.Infrastructure.Telemetry;

/// <summary>
/// UTF-8 limits on the validated wire JSON: one observation object, and its data JSON value.
/// Includes JSON escaping and internal whitespace, excludes the envelope and KV/response escaping.
/// Missing/null data counts as zero. Validate the whole envelope before any publish or Hot write.
/// </summary>
public sealed class TelemetrySizeLimits
{
    public const int DefaultMaxDataBytes = 1024;
    public const int DefaultMaxRecordBytes = 16 * 1024;
    public static TelemetrySizeLimits Default { get; } = new();
    public int MaxDataBytes { get; }
    public int MaxRecordBytes { get; }

    public TelemetrySizeLimits(int maxDataBytes = DefaultMaxDataBytes, int maxRecordBytes = DefaultMaxRecordBytes)
    {
        if (maxDataBytes <= 0) throw new ArgumentOutOfRangeException(nameof(maxDataBytes));
        if (maxRecordBytes <= 0 || maxRecordBytes < maxDataBytes)
            throw new ArgumentOutOfRangeException(nameof(maxRecordBytes));
        MaxDataBytes = maxDataBytes;
        MaxRecordBytes = maxRecordBytes;
    }

    public void Validate(string message, string source = "unknown")
    {
        using var document = JsonDocument.Parse(message);
        try
        {
            foreach (var entity in document.RootElement.GetProperty("telemetries").EnumerateArray())
            {
                Check("record", Encoding.UTF8.GetByteCount(entity.GetRawText()), MaxRecordBytes);
                if (entity.TryGetProperty("data", out var data) && data.ValueKind != JsonValueKind.Null)
                    Check("data", Encoding.UTF8.GetByteCount(data.GetRawText()), MaxDataBytes);
            }
        }
        catch (TelemetrySizeLimitException ex)
        {
            BuildingOsMetrics.TelemetrySizeRejections.Add(1,
                new KeyValuePair<string, object?>("source", source),
                new KeyValuePair<string, object?>("dimension", ex.Dimension));
            throw;
        }
    }

    public static TelemetrySizeLimits Parse(string? maxDataBytes, string? maxRecordBytes) => new(
        ParseBytes(maxDataBytes, DefaultMaxDataBytes, "TELEMETRY_MAX_DATA_BYTES"),
        ParseBytes(maxRecordBytes, DefaultMaxRecordBytes, "TELEMETRY_MAX_RECORD_BYTES"));

    private static int ParseBytes(string? raw, int fallback, string name)
    {
        if (raw is null) return fallback;
        if (!int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value <= 0)
            throw new ArgumentException($"{name} must be a positive integer byte count.", name);
        return value;
    }

    private static void Check(string dimension, int actual, int limit)
    {
        if (actual > limit) throw new TelemetrySizeLimitException(dimension, actual, limit);
    }
}

/// <summary>No payload, identifiers or attribute values are included in this rejection.</summary>
public sealed class TelemetrySizeLimitException(string dimension, int actualBytes, int limitBytes)
    : InvalidOperationException($"Telemetry {dimension} exceeds byte limit ({actualBytes} > {limitBytes}).")
{
    public string Dimension { get; } = dimension;
    public int ActualBytes { get; } = actualBytes;
    public int LimitBytes { get; } = limitBytes;
}
