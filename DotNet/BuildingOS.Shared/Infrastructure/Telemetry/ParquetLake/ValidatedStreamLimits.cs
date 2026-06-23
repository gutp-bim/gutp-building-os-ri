using NATS.Client.JetStream.Models;

namespace BuildingOS.Shared.Infrastructure.Telemetry.ParquetLake;

/// <summary>
/// Builds the explicit limits for the <c>BUILDING_OS_VALIDATED</c> stream (#213). The default config
/// is unbounded (LimitsPolicy, no MaxAge), so under sustained load the server file store can fill and
/// Discard(Old) drops messages before the writer acks them. The Parquet lake writer provisions the
/// stream with a MaxAge well beyond the flush interval + AckWait so an un-acked window is never lost,
/// and an optional MaxBytes cap.
/// </summary>
public static class ValidatedStreamLimits
{
    public static StreamConfig Apply(StreamConfig config, TimeSpan maxAge, long maxBytes)
    {
        // MaxAge must comfortably exceed flush interval + AckWait (the un-acked window). Default 24h.
        config.MaxAge = maxAge;
        if (maxBytes > 0)
        {
            config.MaxBytes = maxBytes;
        }
        config.Retention = StreamConfigRetention.Limits;
        config.Discard = StreamConfigDiscard.Old;
        return config;
    }
}
