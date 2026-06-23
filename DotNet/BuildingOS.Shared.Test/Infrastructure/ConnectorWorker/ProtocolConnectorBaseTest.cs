using BuildingOS.ConnectorWorker.Connectors;
using BuildingOS.Shared.Infrastructure.ConnectorWorker;
using BuildingOS.Shared.Infrastructure.Messaging;
using BuildingOS.Shared.Module;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;

namespace BuildingOS.Shared.Test.Infrastructure.ConnectorWorker;

/// <summary>
/// Verifies the shared ProcessAsync loop in ProtocolConnectorBase using the concrete
/// migrated workers (Hvac, Electric) with InProcessMessageSubscription and a
/// mocked IPointIdFactory. Same pattern as ConnectorWorkerBaseTest.
/// </summary>
public class ProtocolConnectorBaseTest
{
    private const string KnownPointId = "point-abc-123";

    // ── HVAC ──────────────────────────────────────────────────────────────────

    private const string HvacMessage = """
        {
          "telemetryData": [{"unitId": "unit-001", "unitName": "Room A", "ambientTemp": 23.5}],
          "deviceId": "hvacWriter_192.168.1.1",
          "connTime": "2025-01-15T12:00:00+09:00",
          "acqTime":  "2025-01-15T12:00:00+09:00",
          "ipAddress": "192.168.1.1"
        }
        """;

    [Fact]
    public async Task Hvac_KnownUnit_PublishesValidTelemetry()
    {
        var (publisher, sub, worker) = CreateHvac("hvac", "unit-001");
        using var cts = new CancellationTokenSource();
        _ = worker.StartAsync(cts.Token);
        await sub.DispatchAsync(HvacMessage, cts.Token);
        await cts.CancelAsync();

        Assert.Single(publisher.Published);
        var json = JsonDocument.Parse(publisher.Published[0].Message).RootElement;
        var entity = json.GetProperty("telemetries")[0];
        Assert.Equal(KnownPointId, entity.GetProperty("point_id").GetString());
        Assert.Equal(23.5, entity.GetProperty("value").GetDouble(), precision: 4);
    }

    [Fact]
    public async Task Hvac_UnknownUnit_PublishesNothing()
    {
        var factory = new Mock<IPointIdFactory>();
        factory.Setup(f => f.TryGetPointIdAsync(It.IsAny<string>(), It.IsAny<string>()))
               .ReturnsAsync((false, Array.Empty<string>()));

        var (publisher, sub, worker) = CreateHvac(factory);
        using var cts = new CancellationTokenSource();
        _ = worker.StartAsync(cts.Token);
        await sub.DispatchAsync(HvacMessage, cts.Token);
        await cts.CancelAsync();

        Assert.Empty(publisher.Published);
    }

    [Fact]
    public async Task Hvac_InvalidJson_PublishesNothing()
    {
        var (publisher, sub, worker) = CreateHvac("hvac", "unit-001");
        using var cts = new CancellationTokenSource();
        _ = worker.StartAsync(cts.Token);
        await sub.DispatchAsync("{not valid}", cts.Token);
        await cts.CancelAsync();

        Assert.Empty(publisher.Published);
    }

    // ── Electric ──────────────────────────────────────────────────────────────

    private const string ElectricMessage = """
        {
          "telemetryData": [
            {"deviceId": "elec-001", "fiap_id": "f-001", "name": "Power Meter 1",
             "time": "2025-01-15T12:00:00Z", "value": 150.5}
          ],
          "connTime": "2025-01-15T12:00:00Z",
          "acqTime":  "2025-01-15T12:00:00Z"
        }
        """;

    [Fact]
    public async Task Electric_KnownDevice_PublishesValidTelemetry()
    {
        var (publisher, sub, worker) = CreateElectric("electric", "elec-001");
        using var cts = new CancellationTokenSource();
        _ = worker.StartAsync(cts.Token);
        await sub.DispatchAsync(ElectricMessage, cts.Token);
        await cts.CancelAsync();

        Assert.Single(publisher.Published);
        var json = JsonDocument.Parse(publisher.Published[0].Message).RootElement;
        var entity = json.GetProperty("telemetries")[0];
        Assert.Equal(KnownPointId, entity.GetProperty("point_id").GetString());
        Assert.Equal(150.5, entity.GetProperty("value").GetDouble(), precision: 4);
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static (FakeNatsPublisher, InProcessMessageSubscription, HvacConnectorWorker) CreateHvac(
        string protocolTag, string localId)
    {
        var factory = new Mock<IPointIdFactory>();
        factory.Setup(f => f.TryGetPointIdAsync(protocolTag, localId))
               .ReturnsAsync((true, new[] { KnownPointId }));
        return CreateHvac(factory);
    }

    private static (FakeNatsPublisher, InProcessMessageSubscription, HvacConnectorWorker) CreateHvac(
        Mock<IPointIdFactory> factory)
    {
        var publisher = new FakeNatsPublisher();
        var sub = new InProcessMessageSubscription();
        var worker = new HvacConnectorWorker(sub, publisher, factory.Object,
            NullLogger<HvacConnectorWorker>.Instance);
        return (publisher, sub, worker);
    }

    private static (FakeNatsPublisher, InProcessMessageSubscription, ElectricConnectorWorker) CreateElectric(
        string protocolTag, string localId)
    {
        var factory = new Mock<IPointIdFactory>();
        factory.Setup(f => f.TryGetPointIdAsync(protocolTag, localId))
               .ReturnsAsync((true, new[] { KnownPointId }));
        var publisher = new FakeNatsPublisher();
        var sub = new InProcessMessageSubscription();
        var worker = new ElectricConnectorWorker(sub, publisher, factory.Object,
            NullLogger<ElectricConnectorWorker>.Instance);
        return (publisher, sub, worker);
    }
}
