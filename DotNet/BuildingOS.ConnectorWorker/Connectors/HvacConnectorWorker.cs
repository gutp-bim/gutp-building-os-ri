using BuildingOS.Shared.Entities;
using BuildingOS.Shared.Infrastructure.ConnectorWorker;
using BuildingOS.Shared.Infrastructure.Messaging;
using BuildingOS.Shared.Module;
using Corvus.Json;
using Microsoft.Extensions.Logging;

namespace BuildingOS.ConnectorWorker.Connectors;

public sealed class HvacConnectorWorker(
    IMessageSubscription subscription,
    INatsPublisher publisher,
    IPointIdFactory pointIdFactory,
    ILogger<HvacConnectorWorker> logger)
    : ProtocolConnectorBase(subscription, publisher, pointIdFactory, "hvac", logger)
{
    protected override Task<IReadOnlyList<ProtocolReading>?> ExtractReadingsAsync(
        string rawMessage, CancellationToken cancellationToken)
    {
        var json = HvacDeviceMessageJson.Parse(rawMessage);
        if (!json.IsValid()) return Task.FromResult<IReadOnlyList<ProtocolReading>?>(null);

        var readings = json.TelemetryData.EnumerateArray()
            .Select(x =>
            {
                if (!x.UnitId.TryGetString(out var unitId)) unitId = x.UnitId.ToString();
                return new ProtocolReading(
                    LocalId: unitId,
                    Value: x.AmbientTemp,
                    Datetime: json.ConnTime,
                    Name: x.UnitName.GetString() ?? string.Empty,
                    DeviceId: json.DeviceId.GetString() ?? string.Empty,
                    Data: new ValidMessageJson.ValidTelemetryEntity.DataEntity([
                        new JsonObjectProperty("ipAddress", json.IpAddress)
                    ]));
            })
            .ToList();

        return Task.FromResult<IReadOnlyList<ProtocolReading>?>(readings);
    }
}
