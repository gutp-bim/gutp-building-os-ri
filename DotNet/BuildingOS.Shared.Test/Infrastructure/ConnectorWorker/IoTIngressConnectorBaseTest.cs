using System.Diagnostics.Metrics;
using BuildingOS.ConnectorWorker.Connectors;
using BuildingOS.Shared.Infrastructure.ConnectorWorker;
using BuildingOS.Shared.Infrastructure.Messaging;
using BuildingOS.Shared.Infrastructure.Oss;
using BuildingOS.Shared.Infrastructure.Telemetry;
using BuildingOS.Shared.Module;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace BuildingOS.Shared.Test.Infrastructure.ConnectorWorker;

/// <summary>
/// #418: IoTIngressConnectorBase.ExtractTimestamp (the MQTT/Hono ingress path) falls back to the
/// envelope's real message-receipt time when the payload carries no usable timestamp. That fallback
/// must be observable — a warning log naming the device plus the shared IngressTimestampFallbacks
/// counter (tags: source, gateway) — and must not fire for a valid payload timestamp. Uses the same
/// InProcessMessageSubscription/FakeNatsPublisher/Mock&lt;IPointIdFactory&gt; pattern as
/// ProtocolConnectorBaseTest, driving the concrete MqttConnectorWorker to exercise the shared base.
/// </summary>
public class IoTIngressConnectorBaseTest
{
    private const string KnownPointId = "PT-mqtt-001";
    private const string FallbackInstrumentName = "building_os.ingress.timestamp_fallbacks";

    private static string Envelope(string deviceId, string? timestampJson) =>
        $$"""
        {
          "topic": "telemetry/{{deviceId}}",
          "tenant": "t1",
          "deviceId": "{{deviceId}}",
          "payload": {"value": 23.5{{(timestampJson is null ? "" : $", \"timestamp\": \"{timestampJson}\"")}}},
          "receivedAt": "2025-01-15T12:00:05Z"
        }
        """;

    private static (FakeNatsPublisher publisher, InProcessMessageSubscription sub, MqttConnectorWorker worker, RecordingLogger<MqttConnectorWorker> logger)
        CreateWorker(string deviceId)
    {
        var publisher = new FakeNatsPublisher();
        var (sub, worker, logger) = CreateWorkerWith(publisher, deviceId);
        return (publisher, sub, worker, logger);
    }

    private static (InProcessMessageSubscription sub, MqttConnectorWorker worker, RecordingLogger<MqttConnectorWorker> logger)
        CreateWorkerWith(INatsPublisher publisher, string deviceId)
    {
        var factory = new Mock<IPointIdFactory>();
        factory.Setup(f => f.TryGetPointIdAsync("mqtt", $"t1/{deviceId}"))
               .ReturnsAsync((true, new[] { KnownPointId }));
        var sub = new InProcessMessageSubscription();
        var logger = new RecordingLogger<MqttConnectorWorker>();
        var worker = new MqttConnectorWorker(sub, publisher, factory.Object, logger);
        return (sub, worker, logger);
    }

    [Fact]
    public async Task MissingTimestamp_IncrementsFallbackMetricAndLogsWarning()
    {
        const string deviceId = "dev-ts-missing";
        var (publisher, sub, worker, logger) = CreateWorker(deviceId);
        using var cts = new CancellationTokenSource();
        _ = worker.StartAsync(cts.Token);

        var fallbackCount = await RunCapturingFallbackCountAsync(deviceId, async () =>
        {
            await sub.DispatchAsync(Envelope(deviceId, timestampJson: null), cts.Token);
        });
        await cts.CancelAsync();

        Assert.Single(publisher.Published);
        Assert.Equal(1, fallbackCount);
        Assert.Contains(logger.Warnings, w => w.Contains(deviceId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnparsableTimestamp_IncrementsFallbackMetric()
    {
        const string deviceId = "dev-ts-bad";
        var (publisher, sub, worker, logger) = CreateWorker(deviceId);
        using var cts = new CancellationTokenSource();
        _ = worker.StartAsync(cts.Token);

        var fallbackCount = await RunCapturingFallbackCountAsync(deviceId, async () =>
        {
            await sub.DispatchAsync(Envelope(deviceId, timestampJson: "not-a-timestamp"), cts.Token);
        });
        await cts.CancelAsync();

        Assert.Single(publisher.Published);
        Assert.Equal(1, fallbackCount);
        Assert.Contains(logger.Warnings, w => w.Contains(deviceId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ValidTimestamp_DoesNotIncrementFallbackMetric()
    {
        const string deviceId = "dev-ts-ok";
        var (publisher, sub, worker, logger) = CreateWorker(deviceId);
        using var cts = new CancellationTokenSource();
        _ = worker.StartAsync(cts.Token);

        var fallbackCount = await RunCapturingFallbackCountAsync(deviceId, async () =>
        {
            await sub.DispatchAsync(Envelope(deviceId, timestampJson: "2025-01-15T12:00:00Z"), cts.Token);
        });
        await cts.CancelAsync();

        Assert.Single(publisher.Published);
        Assert.Equal(0, fallbackCount);
        Assert.Empty(logger.Warnings);
    }

    /// <summary>
    /// #415 — <b>characterization test, written after the implementation</b> (not part of the RED
    /// phase): it pins <i>where</i> the event-time lag is measured rather than driving a new
    /// behaviour. The measurement sits in <c>ValidatedTelemetryHotStore</c>, i.e. on the hot-store
    /// sync side, not in the connector's publish. So a connector wired to a bare publisher records
    /// nothing, and the same connector wired through the real <c>NatsKvPublisher</c> decorator — which
    /// is how every connector is actually composed in
    /// <c>ConnectorWorkerServiceCollectionExtensions</c> — records the reading's own age.
    /// Moving the measurement to the publish side would break this test.
    ///
    /// The measurement is matched by an event age (7h) no other test in this assembly produces, since
    /// the instrument is a process-wide static and test classes run in parallel.
    /// </summary>
    [Fact]
    public async Task EventLag_IsRecordedOnTheHotStoreSyncSide_NotOnThePublishSide()
    {
        const string deviceId = "dev-lag-pin";
        var eventTime = DateTimeOffset.UtcNow.AddHours(-7);
        var hot = new RecordingHotStore();

        var withoutDecorator = await CaptureSevenHourEventLagsAsync(async () =>
        {
            var (sub, worker, _) = CreateWorkerWith(new FakeNatsPublisher(), deviceId);
            using var cts = new CancellationTokenSource();
            _ = worker.StartAsync(cts.Token);
            await sub.DispatchAsync(Envelope(deviceId, eventTime.ToString("O")), cts.Token);
            await cts.CancelAsync();
        });

        var withDecorator = await CaptureSevenHourEventLagsAsync(async () =>
        {
            var decorated = new NatsKvPublisher(new FakeNatsPublisher(), hot, NullLogger<NatsKvPublisher>.Instance);
            var (sub, worker, _) = CreateWorkerWith(decorated, deviceId);
            using var cts = new CancellationTokenSource();
            _ = worker.StartAsync(cts.Token);
            await sub.DispatchAsync(Envelope(deviceId, eventTime.ToString("O")), cts.Token);
            await cts.CancelAsync();
        });

        Assert.Empty(withoutDecorator);
        Assert.NotEmpty(withDecorator);
        Assert.Equal(new[] { KnownPointId }, hot.Put);
    }

    private sealed class RecordingHotStore : IHotTelemetryStore
    {
        public List<string> Put { get; } = new();

        public Task PutAsync(string pointId, ValidTelemetryData data, CancellationToken cancellationToken = default)
        {
            Put.Add(pointId);
            return Task.CompletedTask;
        }

        public Task<ValidTelemetryData?> GetAsync(string pointId, CancellationToken cancellationToken = default)
            => Task.FromResult<ValidTelemetryData?>(null);
    }

    /// <summary>Captures building_os.ingress.event_lag measurements in the ~7h band this test uses.</summary>
    private static async Task<List<double>> CaptureSevenHourEventLagsAsync(Func<Task> run)
    {
        var values = new List<double>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == OtelSetup.MeterName && instrument.Name == "building_os.ingress.event_lag")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<double>((_, measurement, _, _) =>
        {
            if (measurement is > 25_000 and < 25_500) values.Add(measurement);
        });
        listener.Start();

        await run();
        return values;
    }

    /// <summary>Same MeterListener-capture pattern as GatewayIngressServiceTest, filtered by the `gateway` tag.</summary>
    private static async Task<int> RunCapturingFallbackCountAsync(string deviceId, Func<Task> run)
    {
        var count = 0;
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == OtelSetup.MeterName && instrument.Name == FallbackInstrumentName)
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, measurement, tags, _) =>
        {
            string? gateway = null;
            foreach (var tag in tags)
            {
                if (tag.Key == "gateway") gateway = tag.Value?.ToString();
            }
            if (gateway == deviceId) count += (int)measurement;
        });
        listener.Start();

        await run();
        return count;
    }

    /// <summary>Minimal ILogger capturing Warning-level messages for assertion, without a mocking lib.
    /// Same pattern as GatewayIngressServiceTest's RecordingLogger.</summary>
    private sealed class RecordingLogger<T> : ILogger<T>
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
}
