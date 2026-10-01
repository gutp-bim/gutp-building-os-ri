using System.Globalization;

namespace BuildingOs.ApiServer.Telemetry;

/// <summary>
/// 24 h receive counts per 15-minute bucket for one point (#551) — the server-side half of the Point
/// detail's 24 h coverage bar (#457). The web client used to download the raw readings and count them
/// itself, which it skips for points whose 24 h would exceed ~2,000 rows (interval ≲ 43 s); a 5-second
/// point has ~17,000. Counting here keeps the wire to 96 integers whatever the rate.
///
/// <para>Buckets are oldest first, aligned to <see cref="WindowEnd"/> (not the wall clock) and
/// half-open <c>[start, start + 15 min)</c> — the same layout as <c>bucketCoverage</c> in
/// <c>web-client/src/lib/telemetry/coverage.ts</c>, which turns them into a coverage ratio using the
/// point's expected interval.</para>
/// </summary>
public sealed record TelemetryCoverage(
    string PointId,
    DateTimeOffset WindowStart,
    DateTimeOffset WindowEnd,
    int BucketSeconds,
    int[] Counts)
{
    public const int DefaultBucketSeconds = 15 * 60;
    public const int DefaultBucketCount = 96;

    /// <summary>
    /// Count <paramref name="timestamps"/> into the window ending at <paramref name="windowEnd"/>.
    /// Readings outside the window, or whose timestamp cannot be parsed, are dropped.
    /// </summary>
    public static TelemetryCoverage Count(
        IEnumerable<string?> timestamps, DateTimeOffset windowEnd, string pointId = "")
    {
        var bucket = TimeSpan.FromSeconds(DefaultBucketSeconds);
        var windowStart = windowEnd - bucket * DefaultBucketCount;
        var counts = new int[DefaultBucketCount];
        foreach (var raw in timestamps)
        {
            if (!DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t))
                continue;
            if (t < windowStart || t >= windowEnd) continue;
            counts[(int)((t - windowStart).Ticks / bucket.Ticks)]++;
        }
        return new TelemetryCoverage(pointId, windowStart, windowEnd, DefaultBucketSeconds, counts);
    }
}
