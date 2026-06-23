namespace BuildingOS.Shared.Infrastructure.Telemetry.ParquetLake;

/// <summary>Reads messages from the validated telemetry JetStream since a given UTC timestamp, returning decoded rows for the specified point.</summary>
public interface IJetStreamTailReader
{
    /// <param name="since">Fetch messages with an event time &gt;= this UTC timestamp.</param>
    /// <param name="pointId">Filter rows to this point id.</param>
    /// <param name="maxMsgs">Cap on messages to fetch in one call.</param>
    /// <param name="timeout">Maximum time to wait for messages from JetStream.</param>
    Task<ValidTelemetryData[]> ReadSinceAsync(
        DateTime since, string pointId, int maxMsgs, TimeSpan timeout, CancellationToken ct);
}
