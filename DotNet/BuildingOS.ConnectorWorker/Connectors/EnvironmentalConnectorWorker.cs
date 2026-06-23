using BuildingOS.Shared.Entities;
using BuildingOS.Shared.Helpers;
using BuildingOS.Shared.Infrastructure.ConnectorWorker;
using BuildingOS.Shared.Infrastructure.Messaging;
using BuildingOS.Shared.Module;
using Corvus.Json;
using Microsoft.Extensions.Logging;

namespace BuildingOS.ConnectorWorker.Connectors;

public sealed class EnvironmentalConnectorWorker(
    IMessageSubscription subscription,
    INatsPublisher publisher,
    IPointIdFactory pointIdFactory,
    ILogger<EnvironmentalConnectorWorker> logger)
    : ConnectorWorkerBase(subscription, publisher, "building-os.validated.telemetry", logger)
{
    protected override async Task<string?> ProcessAsync(string rawMessage, CancellationToken cancellationToken)
    {
        var json = EnvironmentalDeviceMessageJson.Parse(rawMessage);
        if (!json.IsValid()) return null;

        var time = json.Logtimestamp;
        var sensorsArray = json.Sensors.EnumerateArray().ToArray();

        var batches = await Task.WhenAll(sensorsArray.Select(async x =>
        {
            var (found, pointIds) = await pointIdFactory.TryGetPointIdAsync(
                "environmental", x.Code.GetString()!);
            if (!found) return Array.Empty<JsonAny>();

            var type = x.Type.GetString();
            return type switch
            {
                "CO2" => pointIds.Select(pid =>
                {
                    var value = pid.Contains("CO2") ? x.Co2.AsNumber
                              : pid.Contains("Humidity") ? x.Humidity.AsNumber
                              : x.Temperature.AsNumber;
                    return CreateEntity(x, pid, value, time);
                }).ToArray(),
                "LUM" => [CreateEntity(x, pointIds.First(), x.Illuminance.AsNumber, time)],
                _ => Array.Empty<JsonAny>()
            };
        }));

        var telemetries = batches.SelectMany(b => b).ToArray();
        if (telemetries.Length == 0) return null;

        return ValidMessageJson.Create(new ValidMessageJson.ValidTelemetryEntityArray(telemetries)).ToString();
    }

    private static JsonAny CreateEntity(
        EnvironmentalDeviceMessageJson.EnvironmentalDeviceTelemetryEntity data,
        string pointId, JsonNumber value, JsonDateTime time) =>
        ValidMessageJson.ValidTelemetryEntity.Create(
            id: $"{pointId}.{DateTime.UtcNow.ToUnixTime()}",
            pointId: pointId,
            building: new JsonString(string.Empty),
            datetime: time,
            value: value,
            name: data.Code.GetString() ?? string.Empty,
            deviceId: data.Code,
            data: new ValidMessageJson.ValidTelemetryEntity.DataEntity([
                new JsonObjectProperty("code", data.Code),
                new JsonObjectProperty("type", data.Type)
            ]));
}
