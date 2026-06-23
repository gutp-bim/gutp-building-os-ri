using BuildingOS.Shared.Entities;
using BuildingOS.Shared.Infrastructure.ConnectorWorker;
using BuildingOS.Shared.Infrastructure.Messaging;
using BuildingOS.Shared.Module;
using Corvus.Json;
using Microsoft.Extensions.Logging;

namespace BuildingOS.ConnectorWorker.Connectors;

public sealed class ElectricConnectorWorker(
    IMessageSubscription subscription,
    INatsPublisher publisher,
    IPointIdFactory pointIdFactory,
    ILogger<ElectricConnectorWorker> logger)
    : ProtocolConnectorBase(subscription, publisher, pointIdFactory, "electric", logger)
{
    protected override Task<IReadOnlyList<ProtocolReading>?> ExtractReadingsAsync(
        string rawMessage, CancellationToken cancellationToken)
    {
        var json = ElectricDeviceMessageJson.Parse(rawMessage);
        if (!json.IsValid()) return Task.FromResult<IReadOnlyList<ProtocolReading>?>(null);

        var readings = json.TelemetryData.EnumerateArray()
            .Select(x => new ProtocolReading(
                LocalId: x.DeviceId.GetString() ?? string.Empty,
                Value: x.Value,
                Datetime: json.ConnTime,
                Name: x.Name.GetString() ?? string.Empty,
                DeviceId: x.DeviceId.GetString() ?? string.Empty,
                Data: new ValidMessageJson.ValidTelemetryEntity.DataEntity([
                    new JsonObjectProperty("deviceId", x.DeviceId)
                ])))
            .ToList();

        return Task.FromResult<IReadOnlyList<ProtocolReading>?>(readings);
    }
}
