using System.Diagnostics.Metrics;
using BuildingOS.Shared.Infrastructure.Oss;
using BuildingOS.Shared.Infrastructure.Telemetry;
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

    /// <summary>Records what was put; optionally throws for a chosen point id to simulate a stalled KV.</summary>
    private sealed class FakeHotStore(string? throwForPointId = null) : IHotTelemetryStore
    {
        public List<string> Put { get; } = new();

        public Task PutAsync(string pointId, ValidTelemetryData data, CancellationToken cancellationToken = default)
        {
            if (pointId == throwForPointId) throw new InvalidOperationException("KV put failed");
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
