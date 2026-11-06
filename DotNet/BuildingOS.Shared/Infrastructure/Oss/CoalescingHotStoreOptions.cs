namespace BuildingOS.Shared.Infrastructure.Oss;

/// <summary>
/// Settings for <see cref="CoalescingHotTelemetryStore"/> (#625 slice A). Parsed from the
/// ConnectorWorker environment: <c>HOT_STORE_COALESCE_MS</c> (default 100; <c>0</c> = no coalescing,
/// every put is awaited synchronously as before) and <c>HOT_STORE_COALESCE_MAX_POINTS</c> (default
/// 5000; a flush is triggered early once this many points are pending).
/// </summary>
public sealed record CoalescingHotStoreOptions
{
    public const int DefaultIntervalMs = 100;
    public const int DefaultMaxPoints = 5000;
    public const int DefaultFlushConcurrency = 32;

    /// <summary>Flush cadence. <see cref="TimeSpan.Zero"/> disables coalescing (synchronous pass-through).</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromMilliseconds(DefaultIntervalMs);

    /// <summary>Pending-point count that triggers a flush before the interval elapses.</summary>
    public int MaxPoints { get; init; } = DefaultMaxPoints;

    /// <summary>Maximum concurrent inner puts during one flush.</summary>
    public int FlushConcurrency { get; init; } = DefaultFlushConcurrency;

    /// <summary>How long the final flush on shutdown may take before pending puts are abandoned.</summary>
    public TimeSpan ShutdownFlushTimeout { get; init; } = TimeSpan.FromSeconds(5);

    public bool Enabled => Interval > TimeSpan.Zero;

    /// <summary>
    /// Builds options from the raw env values. A non-numeric or negative interval falls back to the
    /// default; <c>0</c> is honoured as "disabled". A non-positive / non-numeric max-points falls back
    /// to the default.
    /// </summary>
    public static CoalescingHotStoreOptions Parse(string? intervalMsRaw, string? maxPointsRaw)
    {
        var intervalMs = int.TryParse(intervalMsRaw?.Trim(), out var ms) && ms >= 0 ? ms : DefaultIntervalMs;
        var maxPoints = int.TryParse(maxPointsRaw?.Trim(), out var mp) && mp > 0 ? mp : DefaultMaxPoints;
        return new CoalescingHotStoreOptions
        {
            Interval = TimeSpan.FromMilliseconds(intervalMs),
            MaxPoints = maxPoints,
        };
    }
}
