using System.Text.Json;
using System.Diagnostics.Metrics;
using BuildingOS.Shared.Infrastructure.ConnectorWorker;
using BuildingOS.Shared.Infrastructure.Oss;
using BuildingOS.Shared.Infrastructure.Telemetry;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using BuildingOS.ConnectorWorker.Connectors;
using NATS.Client.JetStream;
using BuildingOS.ConnectorWorker.Startup;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace BuildingOS.Shared.Test.Infrastructure.Oss;

public class TelemetrySizeLimitsTest
{
    [Fact]
    public async Task Publisher_RejectsEntireBatchBeforePublishOrHotWrite_WhenDataExceedsDefault()
    {
        var inner = new Mock<INatsPublisher>(MockBehavior.Strict);
        var hot = new Mock<IHotTelemetryStore>(MockBehavior.Strict);
        var publisher = new NatsKvPublisher(inner.Object, hot.Object, NullLogger<NatsKvPublisher>.Instance);
        var message = JsonSerializer.Serialize(new { telemetries = new[]
        {
            new { point_id = "small", value = 1, data = new { note = "ok" } },
            new { point_id = "large", value = 2, data = new { note = new string('x', 1024) } },
        }});

        await Assert.ThrowsAsync<TelemetrySizeLimitException>(() => publisher.PublishAsync(
            "building-os.validated.telemetry", message, CancellationToken.None));
        inner.VerifyNoOtherCalls();
        hot.VerifyNoOtherCalls();
    }
    [Fact]
    public async Task Publisher_RejectsOversizedStringValueWithoutData()
    {
        var inner = new Mock<INatsPublisher>();
        var hot = new Mock<IHotTelemetryStore>();
        var publisher = new NatsKvPublisher(inner.Object, hot.Object, NullLogger<NatsKvPublisher>.Instance);
        var message = JsonSerializer.Serialize(new { telemetries = new[]
        {
            new { point_id = "large", value = new string('x', 16 * 1024) },
        }});
        await Assert.ThrowsAsync<TelemetrySizeLimitException>(() => publisher.PublishAsync(
            "building-os.validated.telemetry", message, CancellationToken.None));
        inner.VerifyNoOtherCalls();
        hot.VerifyNoOtherCalls();
    }
    [Fact]
    public async Task Publisher_UsesConfiguredUtf8DataLimit_IncludingJsonEscaping()
    {
        // JSON bytes: {"x":"温\\\""} is larger than its character count.
        const string data = """{"x":"温\\\""}""";
        var bytes = System.Text.Encoding.UTF8.GetByteCount(data);
        var message = "{\"telemetries\":[{\"point_id\":\"p\",\"value\":1,\"data\":" + data + "}]}";
        var inner = new Mock<INatsPublisher>();
        var hot = new Mock<IHotTelemetryStore>();
        var allowed = new NatsKvPublisher(inner.Object, hot.Object, NullLogger<NatsKvPublisher>.Instance,
            new TelemetrySizeLimits(bytes, 1024));
        await allowed.PublishAsync("building-os.validated.telemetry", message);
        inner.Verify(p => p.PublishAsync("building-os.validated.telemetry", message, It.IsAny<CancellationToken>()), Times.Once);
        inner.Invocations.Clear();
        hot.Invocations.Clear();
        var rejected = new NatsKvPublisher(inner.Object, hot.Object, NullLogger<NatsKvPublisher>.Instance,
            new TelemetrySizeLimits(bytes - 1, 1024));
        var error = await Assert.ThrowsAsync<TelemetrySizeLimitException>(() =>
            rejected.PublishAsync("building-os.validated.telemetry", message));
        Assert.Equal("data", error.Dimension);
        Assert.Equal(bytes, error.ActualBytes);
        inner.VerifyNoOtherCalls();
        hot.VerifyNoOtherCalls();
    }
    [Theory]
    [InlineData("0", "16384")]
    [InlineData("-1", "16384")]
    [InlineData("", "16384")]
    [InlineData("no-limit", "16384")]
    [InlineData("2147483648", "16384")]
    [InlineData("1024", "0")]
    [InlineData("1024", "512")]
    public void Worker_RejectsInvalidSizeConfigurationAtRegistration(string dataLimit, string recordLimit)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TELEMETRY_MAX_DATA_BYTES"] = dataLimit,
            ["TELEMETRY_MAX_RECORD_BYTES"] = recordLimit,
        });
        Assert.ThrowsAny<ArgumentException>(() => builder.AddConnectorWorkerMessaging());
    }
    [Fact]
    public async Task GatewayBus_RejectsOversizedObservationBeforeAnyJetStreamOrHotOperation()
    {
        var js = new Mock<INatsJSContext>(MockBehavior.Strict);
        var hot = new Mock<IHotTelemetryStore>(MockBehavior.Strict);
        var bus = new NatsIngressTelemetryBus(js.Object, hot.Object, NullLogger<NatsIngressTelemetryBus>.Instance);
        var message = JsonSerializer.Serialize(new { telemetries = new[]
        {
            new { point_id = "large", value = 2, data = new { note = new string('x', 1024) } },
        }});
        await Assert.ThrowsAsync<TelemetrySizeLimitException>(() => bus.PublishAsync(
            "building-os.validated.telemetry", message, CancellationToken.None));
        js.VerifyNoOtherCalls();
        hot.VerifyNoOtherCalls();
    }
    [Fact]
    public async Task Publisher_CountsSizeRejectionWithoutPayloadMetricTags()
    {
        var measurements = new List<(long Value, KeyValuePair<string, object?>[] Tags)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, l) =>
        {
            if (instrument.Meter.Name == OtelSetup.MeterName && instrument.Name == "building_os.telemetry.size_rejections")
                l.EnableMeasurementEvents(instrument);
        };
        listener.SetMeasurementEventCallback<long>((_, value, tags, _) => measurements.Add((value, tags.ToArray())));
        listener.Start();
        var publisher = new NatsKvPublisher(Mock.Of<INatsPublisher>(), Mock.Of<IHotTelemetryStore>(),
            NullLogger<NatsKvPublisher>.Instance);
        var message = JsonSerializer.Serialize(new { telemetries = new[] { new { data = new { note = new string('x', 1024) } } } });
        await Assert.ThrowsAsync<TelemetrySizeLimitException>(() => publisher.PublishAsync("building-os.validated.telemetry", message));
        var measurement = Assert.Single(measurements);
        Assert.Equal(1, measurement.Value);
        Assert.Equal(new[] { "dimension", "source" }, measurement.Tags.Select(t => t.Key).Order());
        Assert.Contains(measurement.Tags, t => t.Key == "dimension" && (string?)t.Value == "data");
        Assert.Contains(measurement.Tags, t => t.Key == "source" && (string?)t.Value == "connector");
    }
    [Theory]
    [InlineData(null, null, 1024, 16384)]
    [InlineData("3072", "32768", 3072, 32768)]
    public void Worker_RegistersConfiguredLimits(string? data, string? record, int expectedData, int expectedRecord)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TELEMETRY_MAX_DATA_BYTES"] = data, ["TELEMETRY_MAX_RECORD_BYTES"] = record,
        });
        builder.AddConnectorWorkerMessaging();
        using var services = builder.Services.BuildServiceProvider();
        var limits = services.GetRequiredService<TelemetrySizeLimits>();
        Assert.Equal(expectedData, limits.MaxDataBytes);
        Assert.Equal(expectedRecord, limits.MaxRecordBytes);
    }

    [Theory]
    [InlineData("")]
    [InlineData(",\"data\":null")]
    [InlineData(",\"data\":{}")]
    [InlineData(",\"data\":{ \"x\": \"温\" }")]
    public void RecordLimit_AcceptsExactUtf8Size_RejectsOneByteOver(string data)
    {
        var record = "{\"point_id\":\"p\",\"value\":\"温\"" + data + "}";
        var message = "{\"telemetries\":[" + record + "]}";
        var bytes = System.Text.Encoding.UTF8.GetByteCount(record);
        new TelemetrySizeLimits(24, bytes).Validate(message);
        var error = Assert.Throws<TelemetrySizeLimitException>(() => new TelemetrySizeLimits(24, bytes - 1).Validate(message));
        Assert.Equal("record", error.Dimension);
        Assert.Equal(bytes, error.ActualBytes);
    }

    [Theory]
    [InlineData(1013, true)]
    [InlineData(1014, false)]
    public void DefaultDataLimit_Accepts1024Bytes_Rejects1025Bytes(int textLength, bool accepted)
    {
        var message = JsonSerializer.Serialize(new { telemetries = new[] { new { data = new { note = new string('x', textLength) } } } });
        if (accepted) TelemetrySizeLimits.Default.Validate(message);
        else Assert.Throws<TelemetrySizeLimitException>(() => TelemetrySizeLimits.Default.Validate(message));
    }

    [Fact]
    public async Task RawMessages_BypassObservationLimits()
    {
        var inner = new Mock<INatsPublisher>();
        var hot = new Mock<IHotTelemetryStore>(MockBehavior.Strict);
        var publisher = new NatsKvPublisher(inner.Object, hot.Object, NullLogger<NatsKvPublisher>.Instance);
        var message = new string('x', 17000); // raw protocol payload, not a validated envelope
        await publisher.PublishAsync("building-os.raw.test", message);
        inner.Verify(p => p.PublishAsync("building-os.raw.test", message, It.IsAny<CancellationToken>()), Times.Once);
        hot.VerifyNoOtherCalls();
    }
}
