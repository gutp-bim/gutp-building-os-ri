namespace BuildingOS.Shared.Infrastructure.Telemetry;

/// <summary>Configuration for the JetStream tail-merge feature (#220).</summary>
public sealed record TailMergeOptions
{
    /// <summary>Queries whose end time is within this many seconds of now trigger tail-merge. 0 or less disables tail merge.</summary>
    public int LookbackSec { get; init; } = 900;

    /// <summary>Maximum JetStream messages fetched per tail-merge call.</summary>
    public int MaxMsgs { get; init; } = 2000;

    /// <summary>
    /// Backstop timeout for the tail-merge JetStream fetch. The reader normally returns as soon as it
    /// catches up with the stream (NumPending == 0), so this only bounds the zero-message case (nothing
    /// published since the window start). Kept short so a recent-window query is not delayed when the
    /// live tail is idle — a 3s value made every "ends near now" query pay ~3s. See NatsTailReader.
    /// </summary>
    public TimeSpan FetchTimeout { get; init; } = TimeSpan.FromSeconds(1);
}
