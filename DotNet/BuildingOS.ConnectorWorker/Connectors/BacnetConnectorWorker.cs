using BuildingOS.Shared.Entities;
using BuildingOS.Shared.Helpers;
using BuildingOS.Shared.Infrastructure.ConnectorWorker;
using BuildingOS.Shared.Infrastructure.Messaging;
using BuildingOS.Shared.Module;
using Corvus.Json;
using Microsoft.Extensions.Logging;

namespace BuildingOS.ConnectorWorker.Connectors;

public sealed class BacnetConnectorWorker(
    IMessageSubscription subscription,
    INatsPublisher publisher,
    BacnetPointResolver bacnetPointResolver,
    ILogger<BacnetConnectorWorker> logger)
    : ConnectorWorkerBase(subscription, publisher, "building-os.validated.telemetry", logger)
{
    protected override async Task<string?> ProcessAsync(string rawMessage, CancellationToken cancellationToken)
    {
        var json = BacnetDeviceMessageJson.Parse(rawMessage);
        if (!json.IsValid()) return null;

        var entities = new List<JsonAny>();
        foreach (var deviceMessage in json.EnumerateArray())
        {
            var deviceId = deviceMessage.DeviceId;
            foreach (var valueString in deviceMessage.ValueString.EnumerateArray())
            {
                var pointInfo = await bacnetPointResolver.GetPointInfoAsync(
                    bacnetDeviceId: valueString.BAnetDevice.ToString(),
                    instanceNo: valueString.BAnetObject.Value.InstanceNo.ToString(),
                    objectType: valueString.BAnetObject.Value.ObjectType.ToString());

                if (pointInfo == null) continue;

                entities.Add(ValidMessageJson.ValidTelemetryEntity.Create(
                    id: $"{pointInfo.PointId}.{DateTime.UtcNow.ToUnixTime()}",
                    pointId: pointInfo.PointId,
                    building: new JsonString(string.Empty),
                    datetime: valueString.TimeStamp,
                    value: valueString.Properties.PresentValue.AsNumber,
                    name: pointInfo.PointName,
                    deviceId: deviceId,
                    data: new ValidMessageJson.ValidTelemetryEntity.DataEntity([
                        new JsonObjectProperty("bacnetDeviceId", valueString.BAnetDevice)
                    ])));
            }
        }

        if (entities.Count == 0) return null;
        return ValidMessageJson.Create(
            new ValidMessageJson.ValidTelemetryEntityArray([.. entities])).ToString();
    }
}
