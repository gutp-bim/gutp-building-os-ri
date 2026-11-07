using BuildingOS.ConnectorWorker.Connectors;
using BuildingOS.Shared.Infrastructure.ConnectorWorker;
using BuildingOS.Shared.Infrastructure.Messaging;
using BuildingOS.Shared.Module;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace BuildingOS.Shared.Test.Infrastructure.ConnectorWorker;

/// <summary>
/// The environmental (CO2 / LUM) and behavior connectors had no unit tests; their only coverage was
/// the golden snapshots in <c>tests/golden/connector/</c>, which nothing compared against any more.
/// Same pattern as <see cref="ProtocolConnectorBaseTest"/>.
/// </summary>
public class EnvironmentalBehaviorConnectorTest
{
    // ── Environmental ──────────────────────────────────────────────────────────

    private const string EnvironmentalMessage = """
        {
          "logtimestamp": "2025-01-15T12:00:00+09:00",
          "gateway": "gw-env-01",
          "sensors": [
            {"code": "CO2007", "type": "CO2", "co2": 612, "temperature": 2835, "humidity": 5520},
            {"code": "LUM003", "type": "LUM", "illuminance": 480}
          ]
        }
        """;

    [Fact]
    public async Task Environmental_Co2Sensor_FansOutOneTelemetryPerPoint_ByPointIdKeyword()
    {
        // A CO2 sensor maps to several points; the point id decides which reading it gets.
        var factory = new Mock<IPointIdFactory>();
        factory.Setup(f => f.TryGetPointIdAsync("environmental", "CO2007"))
               .ReturnsAsync((true, new[] { "room1-CO2", "room1-Humidity", "room1-Temperature" }));
        factory.Setup(f => f.TryGetPointIdAsync("environmental", "LUM003"))
               .ReturnsAsync((false, Array.Empty<string>()));

        var entities = await RunEnvironmentalAsync(factory, EnvironmentalMessage);

        var byPoint = entities.ToDictionary(e => e.GetProperty("point_id").GetString()!, e => e);
        Assert.Equal(3, byPoint.Count);
        Assert.Equal(612, byPoint["room1-CO2"].GetProperty("value").GetDouble());
        Assert.Equal(5520, byPoint["room1-Humidity"].GetProperty("value").GetDouble());
        // Values pass through as the device sends them (temperature is Kelvin x 100 per the schema);
        // the connector does no unit conversion.
        Assert.Equal(2835, byPoint["room1-Temperature"].GetProperty("value").GetDouble());

        var co2 = byPoint["room1-CO2"];
        Assert.Equal("CO2007", co2.GetProperty("device_id").GetString());
        Assert.Equal("CO2007", co2.GetProperty("name").GetString());
        Assert.Equal("CO2007", co2.GetProperty("data").GetProperty("code").GetString());
        Assert.Equal("CO2", co2.GetProperty("data").GetProperty("type").GetString());
        Assert.Equal(DateTimeOffset.Parse("2025-01-15T12:00:00+09:00"), co2.GetProperty("datetime").GetDateTimeOffset());
        Assert.StartsWith("room1-CO2.", co2.GetProperty("id").GetString());
    }

    [Fact]
    public async Task Environmental_LumSensor_PublishesIlluminanceToTheFirstPoint()
    {
        var factory = new Mock<IPointIdFactory>();
        factory.Setup(f => f.TryGetPointIdAsync("environmental", "CO2007"))
               .ReturnsAsync((false, Array.Empty<string>()));
        factory.Setup(f => f.TryGetPointIdAsync("environmental", "LUM003"))
               .ReturnsAsync((true, new[] { "room1-Lux", "room1-Lux-spare" }));

        var entity = Assert.Single(await RunEnvironmentalAsync(factory, EnvironmentalMessage));

        Assert.Equal("room1-Lux", entity.GetProperty("point_id").GetString());
        Assert.Equal(480, entity.GetProperty("value").GetDouble());
        Assert.Equal("LUM", entity.GetProperty("data").GetProperty("type").GetString());
    }

    [Fact]
    public async Task Environmental_NoKnownSensor_PublishesNothing()
    {
        var factory = new Mock<IPointIdFactory>();
        factory.Setup(f => f.TryGetPointIdAsync(It.IsAny<string>(), It.IsAny<string>()))
               .ReturnsAsync((false, Array.Empty<string>()));

        Assert.Empty(await RunEnvironmentalAsync(factory, EnvironmentalMessage));
    }

    [Theory]
    [InlineData("{not valid}")]
    [InlineData("""{"gateway": "gw-env-01", "sensors": []}""")] // logtimestamp missing
    public async Task Environmental_InvalidMessage_PublishesNothing(string message)
    {
        var factory = new Mock<IPointIdFactory>();
        factory.Setup(f => f.TryGetPointIdAsync(It.IsAny<string>(), It.IsAny<string>()))
               .ReturnsAsync((true, new[] { "room1-CO2" }));

        Assert.Empty(await RunEnvironmentalAsync(factory, message));
    }

    // ── Behavior ───────────────────────────────────────────────────────────────

    private const string BehaviorMessage = """
        {
          "point_id": "BehaviorSnsr_0001",
          "value": 3,
          "data": {"sbos_space:Name": "102C2"},
          "datetime": "2025-01-23T19:34:00+09:00",
          "building": "Engineering(Bldg.2)",
          "device_id": "BehaviorSnsr_0001"
        }
        """;

    [Fact]
    public async Task Behavior_ValidMessage_PublishesOneTelemetryWithTheSpaceName()
    {
        var entity = Assert.Single(await RunBehaviorAsync(BehaviorMessage));

        Assert.Equal("BehaviorSnsr_0001", entity.GetProperty("point_id").GetString());
        Assert.Equal(3, entity.GetProperty("value").GetDouble());
        Assert.Equal("Engineering(Bldg.2)", entity.GetProperty("building").GetString());
        Assert.Equal("BehaviorSnsr_0001", entity.GetProperty("device_id").GetString());
        Assert.Equal("102C2", entity.GetProperty("data").GetProperty("sbos_space:Name").GetString());
        Assert.Equal(DateTimeOffset.Parse("2025-01-23T19:34:00+09:00"), entity.GetProperty("datetime").GetDateTimeOffset());
        Assert.StartsWith("BehaviorSnsr_0001.", entity.GetProperty("id").GetString());
    }

    [Theory]
    [InlineData("{not valid}")]
    [InlineData("""{"value": 3, "data": {}, "device_id": "d"}""")] // point_id missing
    [InlineData("""{"point_id": "p", "value": 3, "data": {}, "device_id": "d", "extra": 1}""")] // additionalProperties: false
    public async Task Behavior_InvalidMessage_PublishesNothing(string message)
        => Assert.Empty(await RunBehaviorAsync(message));

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static async Task<JsonElement[]> RunEnvironmentalAsync(Mock<IPointIdFactory> factory, string message)
    {
        var publisher = new FakeNatsPublisher();
        var sub = new InProcessMessageSubscription();
        var worker = new EnvironmentalConnectorWorker(sub, publisher, factory.Object,
            NullLogger<EnvironmentalConnectorWorker>.Instance);
        return await DispatchAsync(worker, sub, publisher, message);
    }

    private static async Task<JsonElement[]> RunBehaviorAsync(string message)
    {
        var publisher = new FakeNatsPublisher();
        var sub = new InProcessMessageSubscription();
        var worker = new BehaviorConnectorWorker(sub, publisher, NullLogger<BehaviorConnectorWorker>.Instance);
        return await DispatchAsync(worker, sub, publisher, message);
    }

    private static async Task<JsonElement[]> DispatchAsync(
        Microsoft.Extensions.Hosting.BackgroundService worker, InProcessMessageSubscription sub, FakeNatsPublisher publisher, string message)
    {
        using var cts = new CancellationTokenSource();
        _ = worker.StartAsync(cts.Token);
        await sub.DispatchAsync(message, cts.Token);
        await cts.CancelAsync();

        Assert.True(publisher.Published.Count <= 1);
        if (publisher.Published.Count == 0) return [];
        Assert.Equal("building-os.validated.telemetry", publisher.Published[0].Subject);
        return JsonDocument.Parse(publisher.Published[0].Message).RootElement.GetProperty("telemetries")
            .EnumerateArray().Select(e => e.Clone()).ToArray();
    }
}
