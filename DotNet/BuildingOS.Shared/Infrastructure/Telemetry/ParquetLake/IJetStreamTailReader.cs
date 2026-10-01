namespace BuildingOS.Shared.Infrastructure.Telemetry.ParquetLake;

/// <summary>Reads messages from the validated telemetry JetStream since a given UTC timestamp, returning decoded rows for the specified points.</summary>
public interface IJetStreamTailReader
{
    /// <param name="since">Fetch messages with an event time &gt;= this UTC timestamp.</param>
    /// <param name="pointIds">
    /// Keep rows of these points. A set rather than one id so a multi-point query (#510) scans the
    /// stream once for all its points instead of once per point.
    /// </param>
    /// <param name="maxMsgs">Cap on messages to fetch in one call.</param>
    /// <param name="timeout">Maximum time to wait for messages from JetStream.</param>
    Task<ValidTelemetryData[]> ReadSinceAsync(
        DateTime since, IReadOnlySet<string> pointIds, int maxMsgs, TimeSpan timeout, CancellationToken ct);
}
