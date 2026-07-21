using BuildingOS.Shared.Entities;
using BuildingOS.Shared.Helpers;
using BuildingOS.Shared.Infrastructure.ConnectorWorker;
using BuildingOS.Shared.Infrastructure.Messaging;
using Corvus.Json;
using Microsoft.Extensions.Logging;

namespace BuildingOS.ConnectorWorker.Connectors;

public sealed class BehaviorConnectorWorker(
    IMessageSubscription subscription,
    INatsPublisher publisher,
    ILogger<BehaviorConnectorWorker> logger)
    : ConnectorWorkerBase(subscription, publisher, "building-os.validated.telemetry", logger)
{
    protected override Task<string?> ProcessAsync(string rawMessage, CancellationToken cancellationToken)
    {
        var json = BehaviorSensorMessageJson.Parse(rawMessage);
        if (!json.IsValid()) return Task.FromResult<string?>(null);

        JsonAny[] telemetries = [
            ValidMessage.ValidTelemetryEntity.Create(
                id: $"{json.PointId.GetString()}.{DateTime.UtcNow.ToUnixTime()}",
                pointId: json.PointId,
                building: json.Building,
                datetime: json.Datetime,
                value: json.Value,
                deviceId: json.DeviceId,
                data: new ValidMessage.ValidTelemetryEntity.DataEntity([
                    new JsonObjectProperty("sbos_space:Name", json.Data.SbosSpaceName)
                ]))
        ];

        var result = ValidMessage.Create(new ValidMessage.ValidTelemetryEntityArray(telemetries)).ToString();
        return Task.FromResult<string?>(result);
    }
}
