using BuildingOS.Shared.Entities;
using BuildingOS.Shared.Helpers;
using BuildingOS.Shared.Infrastructure.ConnectorWorker;
using BuildingOS.Shared.Infrastructure.Messaging;
using BuildingOS.Shared.Module;
using Corvus.Json;

namespace BuildingOS.ConnectorWorker.Connectors;

/// <summary>
/// Common base for protocol-specific connectors that follow the pattern:
///   parse schema → extract readings → resolve PointId → build ValidTelemetryEntity.
///
/// Subclasses implement ExtractReadingsAsync and return a list of ProtocolReading.
/// This class handles PointId resolution and ValidMessageJson construction.
///
/// Applies to: HvacConnectorWorker, ElectricConnectorWorker.
/// Does NOT apply to: EnvironmentalConnectorWorker (multi-value), BacnetConnectorWorker
/// (custom resolver), BehaviorConnectorWorker (no PointId resolution).
/// </summary>
public abstract class ProtocolConnectorBase(
    IMessageSubscription subscription,
    INatsPublisher publisher,
    IPointIdFactory pointIdFactory,
    string protocolTag,
    ILogger logger)
    : ConnectorWorkerBase(subscription, publisher, "building-os.validated.telemetry", logger)
{
    /// <summary>
    /// Parse the raw NATS message and return a list of readings to be published.
    /// Return null or an empty list to skip this message.
    /// </summary>
    protected abstract Task<IReadOnlyList<ProtocolReading>?> ExtractReadingsAsync(
        string rawMessage, CancellationToken cancellationToken);

    protected override async Task<string?> ProcessAsync(string rawMessage, CancellationToken cancellationToken)
    {
        var readings = await ExtractReadingsAsync(rawMessage, cancellationToken);
        if (readings is null or { Count: 0 }) return null;

        var entities = await Task.WhenAll(readings.Select(async r =>
        {
            var (found, pointIds) = await pointIdFactory.TryGetPointIdAsync(protocolTag, r.LocalId);
            if (!found) return (JsonAny?)null;

            var pointId = pointIds.First();
            return ValidMessageJson.ValidTelemetryEntity.Create(
                id: $"{pointId}.{DateTime.UtcNow.ToUnixTime()}",
                pointId: pointId,
                building: new JsonString(string.Empty),
                datetime: r.Datetime,
                value: r.Value,
                name: new JsonString(r.Name),
                deviceId: new JsonString(r.DeviceId),
                data: r.Data
            ).AsAny;
        }));

        var telemetries = entities.Where(e => e.HasValue).Select(e => e!.Value).ToArray();
        if (telemetries.Length == 0) return null;

        return ValidMessageJson.Create(new ValidMessageJson.ValidTelemetryEntityArray(telemetries)).ToString();
    }
}
