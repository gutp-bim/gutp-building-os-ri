using System.Diagnostics.Metrics;
using BuildingOS.Shared.Infrastructure.Telemetry;

namespace BuildingOS.Shared.Test.Infrastructure.Telemetry;

/// <summary>
/// #415: the two ingest-lag signals the pipeline had no way to expose before.
/// <c>building_os.ingress.event_lag</c> is event-time lag (now − the accepted reading's own
/// <c>datetime</c>); <c>building_os.ingestion.lag</c> is consumer lag (now − the raw-subject
/// JetStream message's stream timestamp). Both live in <see cref="IngestLagRecorder"/> so they are
/// testable — <c>NatsMessageSubscription</c> casts to a concrete <c>NatsConnection</c> and cannot be
/// unit-tested, which is why the original inline implementation had no coverage.
///
/// The instruments are process-wide statics and BuildingOS.Shared.Test runs test classes in parallel
/// (no xunit.runner.json, no collection definition), so every assertion here filters measurements by
/// a tag value unique to the test rather than counting all measurements on the instrument.
/// </summary>
public class IngestLagRecorderTest
{
    private const string EventLagInstrument = "building_os.ingress.event_lag";
    private const string IngestionLagInstrument = "building_os.ingestion.lag";

    [Fact]
    public void RecordEventLag_PastEventTime_RecordsSecondsBehindTaggedBySource()
    {
        const string source = "test-recorder-past";
        var now = DateTimeOffset.Parse("2026-06-12T12:00:00Z");
        var eventTime = now.AddSeconds(-90).ToString("O");

        var values = Capture(EventLagInstrument, "source", source,
            () => Assert.True(IngestLagRecorder.RecordEventLag(source, eventTime, now)));

        Assert.Equal(90d, Assert.Single(values), precision: 3);
    }

    [Fact]
    public void RecordEventLag_FutureEventTime_ClampsToZero()
    {
        // A gateway clock ahead of ours must not produce a negative lag (it would drag a
        // histogram's average down and hide real backlog).
        const string source = "test-recorder-future";
        var now = DateTimeOffset.Parse("2026-06-12T12:00:00Z");
        var eventTime = now.AddSeconds(30).ToString("O");

        var values = Capture(EventLagInstrument, "source", source,
            () => Assert.True(IngestLagRecorder.RecordEventLag(source, eventTime, now)));

        Assert.Equal(0d, Assert.Single(values));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-timestamp")]
    public void RecordEventLag_MissingOrUnparsableEventTime_RecordsNothingAndReturnsFalse(string? eventTime)
    {
        const string source = "test-recorder-unparsable";
        var now = DateTimeOffset.Parse("2026-06-12T12:00:00Z");

        var values = Capture(EventLagInstrument, "source", source,
            () => Assert.False(IngestLagRecorder.RecordEventLag(source, eventTime, now)));

        Assert.Empty(values);
    }

    [Fact]
    public void RecordConsumerLag_StreamTimestamp_RecordsSecondsBehindTaggedBySubject()
    {
        const string subject = "test.recorder.subject.present";
        var now = DateTimeOffset.Parse("2026-06-12T12:00:00Z");

        var values = Capture(IngestionLagInstrument, "subject", subject,
            () => Assert.True(IngestLagRecorder.RecordConsumerLag(subject, now.AddSeconds(-12), now)));

        Assert.Equal(12d, Assert.Single(values), precision: 3);
    }

    [Fact]
    public void RecordConsumerLag_NoStreamTimestamp_RecordsNothingAndReturnsFalse()
    {
        // JetStream metadata is absent for a core-NATS delivery; there is nothing to measure against.
        const string subject = "test.recorder.subject.absent";
        var now = DateTimeOffset.Parse("2026-06-12T12:00:00Z");

        var values = Capture(IngestionLagInstrument, "subject", subject,
            () => Assert.False(IngestLagRecorder.RecordConsumerLag(subject, streamTimestamp: null, now)));

        Assert.Empty(values);
    }

    /// <summary>
    /// Same MeterListener pattern as ConnectorWorkerBaseMetricsTest, narrowed to the measurements
    /// carrying <paramref name="tagValue"/> on <paramref name="tagKey"/> so concurrently running test
    /// classes writing to the same static instrument cannot pollute the result.
    /// </summary>
    private static List<double> Capture(string instrumentName, string tagKey, string tagValue, Action run)
    {
        var values = new List<double>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == OtelSetup.MeterName && instrument.Name == instrumentName)
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((_, measurement, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag.Key == tagKey && tag.Value?.ToString() == tagValue)
                    values.Add(measurement);
        });
        listener.Start();

        run();
        return values;
    }
}
