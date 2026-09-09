using System.Globalization;

namespace BuildingOS.Shared.Infrastructure.Telemetry;

/// <summary>
/// #415: records the two ingest-lag signals — event-time lag
/// (<see cref="BuildingOsMetrics.IngressEventLag"/>) and consumer lag
/// (<see cref="BuildingOsMetrics.IngestionLag"/>) — for callers that sit on the hot path.
///
/// A pure static helper rather than inline code at each call site: the consumer-lag call site
/// (<c>NatsMessageSubscription</c>) casts its connection to the concrete <c>NatsConnection</c> and so
/// cannot be constructed in a unit test, which is why the original inline version shipped untested.
/// The clamping and parse rules live here instead, where they are coverable.
///
/// Both methods report whether they recorded anything so a caller can tell "measured" from
/// "nothing measurable" without inspecting the meter.
/// </summary>
public static class IngestLagRecorder
{
    /// <summary>
    /// Records how far behind its own event time a reading is when it lands in the hot store.
    /// <paramref name="eventTimestamp"/> is the telemetry's ISO-8601 <c>datetime</c>; a missing or
    /// unparsable value records nothing and returns false (guessing a lag would be worse than a gap).
    /// A source clock running ahead of ours is clamped to 0 rather than recorded as a negative lag.
    /// </summary>
    public static bool RecordEventLag(string source, string? eventTimestamp, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(eventTimestamp)) return false;
        if (!DateTimeOffset.TryParse(
                eventTimestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var eventTime))
        {
            return false;
        }

        BuildingOsMetrics.IngressEventLag.Record(
            Math.Max(0, (now - eventTime).TotalSeconds),
            new KeyValuePair<string, object?>("source", source));
        return true;
    }

    /// <summary>
    /// Records how far behind its stream a JetStream consumer is running.
    /// <paramref name="streamTimestamp"/> is null for a delivery without JetStream metadata (core
    /// NATS), in which case there is nothing to measure against and nothing is recorded.
    /// </summary>
    public static bool RecordConsumerLag(string subject, DateTimeOffset? streamTimestamp, DateTimeOffset now)
    {
        if (streamTimestamp is null) return false;

        BuildingOsMetrics.IngestionLag.Record(
            Math.Max(0, (now - streamTimestamp.Value).TotalSeconds),
            new KeyValuePair<string, object?>("subject", subject));
        return true;
    }
}
