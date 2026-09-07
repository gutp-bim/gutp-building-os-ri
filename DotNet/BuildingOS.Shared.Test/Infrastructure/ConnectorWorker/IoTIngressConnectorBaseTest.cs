using System.Diagnostics.Metrics;
using BuildingOS.ConnectorWorker.Connectors;
using BuildingOS.Shared.Infrastructure.ConnectorWorker;
using BuildingOS.Shared.Infrastructure.Messaging;
using BuildingOS.Shared.Infrastructure.Telemetry;
using BuildingOS.Shared.Module;
using Microsoft.Extensions.Logging;
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
        var factory = new Mock<IPointIdFactory>();
        factory.Setup(f => f.TryGetPointIdAsync("mqtt", $"t1/{deviceId}"))
               .ReturnsAsync((true, new[] { KnownPointId }));
        var publisher = new FakeNatsPublisher();
        var sub = new InProcessMessageSubscription();
        var logger = new RecordingLogger<MqttConnectorWorker>();
        var worker = new MqttConnectorWorker(sub, publisher, factory.Object, logger);
        return (publisher, sub, worker, logger);
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
