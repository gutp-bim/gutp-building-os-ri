using System.Diagnostics.Metrics;
using BuildingOS.Shared.Infrastructure.Oss;
using BuildingOS.Shared.Infrastructure.Telemetry;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BuildingOS.Shared.Test.Infrastructure.Oss;

/// <summary>
/// #415: ValidatedTelemetryHotStore is the one point every validated-telemetry producer passes
/// through — the NatsKvPublisher decorator (all connectors: MQTT/Hono/HVAC/BACnet/…) and the gRPC
/// ingress bus alike — so it is where the end-to-end event-time lag
/// (<c>building_os.ingress.event_lag</c>) is measured, tagged by which of the two produced it.
///
/// The instrument is a process-wide static and this assembly runs test classes in parallel, so every
/// assertion filters measurements by a <c>source</c> tag value unique to the test.
/// </summary>
public class ValidatedTelemetryHotStoreTest
{
    private const string EventLagInstrument = "building_os.ingress.event_lag";

    /// <summary>
    /// Records what was put; optionally throws for a chosen point id to simulate a stalled KV.
    /// Observes the token the way a real store does, so a cancelled token surfaces as an
    /// <see cref="OperationCanceledException"/> out of the put.
    /// </summary>
    private sealed class FakeHotStore(string? throwForPointId = null, Exception? throwWith = null) : IHotTelemetryStore
    {
        public List<string> Put { get; } = new();

        public Task PutAsync(string pointId, ValidTelemetryData data, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (pointId == throwForPointId) throw throwWith ?? new InvalidOperationException("KV put failed");
            Put.Add(pointId);
            return Task.CompletedTask;
        }

        public Task<ValidTelemetryData?> GetAsync(string pointId, CancellationToken cancellationToken = default)
            => Task.FromResult<ValidTelemetryData?>(null);
    }

    private static string Message(params (string PointId, DateTimeOffset Datetime)[] entities) =>
        $$"""
        { "telemetries": [ {{string.Join(",", entities.Select(e =>
            $$"""
            { "point_id": "{{e.PointId}}", "device_id": "d1", "building": "b1", "name": "temp",
              "value": 23.5, "datetime": "{{e.Datetime:O}}" }
            """))}} ] }
        """;

    [Fact]
    public async Task WriteAsync_RecordsEventLag_FromTheReadingsOwnDatetime()
    {
        const string source = "test-hotstore-connector";
        var hot = new FakeHotStore();
        var message = Message(("p1", DateTimeOffset.UtcNow.AddSeconds(-600)));

        var values = await CaptureAsync(source, () =>
            ValidatedTelemetryHotStore.WriteAsync(hot, message, source, NullLogger.Instance, CancellationToken.None));

        Assert.InRange(Assert.Single(values), 590d, 660d);
        Assert.Equal(new[] { "p1" }, hot.Put);
    }

    [Fact]
    public async Task WriteAsync_RecordsEventLag_PerTelemetryEntity()
    {
        const string source = "test-hotstore-multi";
        var hot = new FakeHotStore();
        var now = DateTimeOffset.UtcNow;
        var message = Message(("p1", now.AddSeconds(-60)), ("p2", now.AddSeconds(-120)));

        var values = await CaptureAsync(source, () =>
            ValidatedTelemetryHotStore.WriteAsync(hot, message, source, NullLogger.Instance, CancellationToken.None));

        Assert.Equal(2, values.Count);
        Assert.Equal(new[] { "p1", "p2" }, hot.Put);
    }

    [Fact]
    public async Task WriteAsync_TagsTheSourceItWasGiven()
    {
        // The gRPC ingress bus passes the same "gateway-grpc" source vocabulary GatewayIngressService
        // already uses on building_os.ingress.messages, so the two can be correlated in one query.
        const string source = "test-hotstore-gateway-grpc";
        var hot = new FakeHotStore();
        var message = Message(("p1", DateTimeOffset.UtcNow.AddSeconds(-5)));

        var values = await CaptureAsync(source, () =>
            ValidatedTelemetryHotStore.WriteAsync(hot, message, source, NullLogger.Instance, CancellationToken.None));

        Assert.Single(values);
    }

    [Fact]
    public async Task WriteAsync_MalformedMessage_RecordsNothingAndDoesNotThrow()
    {
        const string source = "test-hotstore-malformed";
        var hot = new FakeHotStore();

        var values = await CaptureAsync(source, () =>
            ValidatedTelemetryHotStore.WriteAsync(hot, "not json", source, NullLogger.Instance, CancellationToken.None));

        Assert.Empty(values);
        Assert.Empty(hot.Put);
    }

    [Fact]
    public async Task WriteAsync_KvPutFailure_StillMeasuresEveryEntity()
    {
        // The whole point of the metric is to be readable while the hot store is struggling. A KV put
        // that throws must neither swallow that entity's measurement (taken before the put) nor abort
        // the remaining entities — otherwise the signal disappears exactly when it is needed.
        const string source = "test-hotstore-kv-failure";
        var hot = new FakeHotStore(throwForPointId: "p1");
        var now = DateTimeOffset.UtcNow;
        var message = Message(("p1", now.AddSeconds(-60)), ("p2", now.AddSeconds(-120)));

        var values = await CaptureAsync(source, () =>
            ValidatedTelemetryHotStore.WriteAsync(hot, message, source, NullLogger.Instance, CancellationToken.None));

        Assert.Equal(2, values.Count);
        Assert.Equal(new[] { "p2" }, hot.Put);
    }

    [Fact]
    public async Task WriteAsync_Cancelled_StopsQuietlyWithoutReportingAHotStoreFailure()
    {
        // Shutdown is not a hot-store failure: a cancelled token must not produce one
        // "hot store sync failed" warning per entity, nor keep walking the remaining entities.
        const string source = "test-hotstore-cancelled";
        var hot = new FakeHotStore();
        var logger = new RecordingLogger();
        var now = DateTimeOffset.UtcNow;
        var message = Message(("p1", now.AddSeconds(-60)), ("p2", now.AddSeconds(-120)));
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var values = await CaptureAsync(source, () =>
            ValidatedTelemetryHotStore.WriteAsync(hot, message, source, logger, cts.Token));

        Assert.Empty(logger.Warnings);
        Assert.Empty(hot.Put);
        // p1's lag is recorded before its put, so it survives; p2 is never reached.
        Assert.Single(values);
    }

    [Fact]
    public async Task WriteAsync_CancellationFromElsewhere_IsStillReportedAndDoesNotStopTheLoop()
    {
        // Only a cancellation of *our* token is a shutdown. An OperationCanceledException raised for an
        // unrelated reason (a KV client's own request timeout, say) is a hot-store failure like any
        // other and must keep the per-entity isolation the metric depends on.
        const string source = "test-hotstore-foreign-cancel";
        var hot = new FakeHotStore(throwForPointId: "p1", throwWith: new OperationCanceledException("KV timed out"));
        var logger = new RecordingLogger();
        var now = DateTimeOffset.UtcNow;
        var message = Message(("p1", now.AddSeconds(-60)), ("p2", now.AddSeconds(-120)));

        var values = await CaptureAsync(source, () =>
            ValidatedTelemetryHotStore.WriteAsync(hot, message, source, logger, CancellationToken.None));

        Assert.Single(logger.Warnings);
        Assert.Equal(new[] { "p2" }, hot.Put);
        Assert.Equal(2, values.Count);
    }

    /// <summary>Minimal ILogger capturing Warning-level messages for assertion, without a mocking lib.
    /// Same pattern as IoTIngressConnectorBaseTest's RecordingLogger.</summary>
    private sealed class RecordingLogger : ILogger
    {
        public List<string> Warnings { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) Warnings.Add(formatter(state, exception));
        }
    }

    /// <summary>MeterListener capture narrowed to one source tag; see the class remarks on parallelism.</summary>
    private static async Task<List<double>> CaptureAsync(string source, Func<Task> run)
    {
        var values = new List<double>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == OtelSetup.MeterName && instrument.Name == EventLagInstrument)
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((_, measurement, tags, _) =>
        {
            foreach (var tag in tags)
                if (tag.Key == "source" && tag.Value?.ToString() == source)
                    values.Add(measurement);
        });
        listener.Start();

        await run();
        return values;
    }
}
