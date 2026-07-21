using BuildingOS.Shared.Entities;
using BuildingOS.Shared.Infrastructure.Telemetry;
using Microsoft.Extensions.Logging;

namespace BuildingOS.Shared.Infrastructure.Oss;

/// <summary>
/// Writes the latest value per point_id from a validated-telemetry message to the hot store. Shared by
/// the <see cref="NatsKvPublisher"/> decorator and the gRPC ingress bus so both keep the hot store in
/// sync identically. KV errors are caught and logged — they must never fail the telemetry publish.
/// </summary>
public static class ValidatedTelemetryHotStore
{
    public static async Task WriteAsync(
        IHotTelemetryStore hot, string message, ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            // Payload is ValidMessage: { "telemetries": [...] } with snake_case keys.
            // Iterate each entity and write the latest value per point_id to the KV store.
            var msg = ValidMessage.Parse(message);
            foreach (var entity in msg.Telemetries.EnumerateArray())
            {
                var te = entity.As<ValidMessage.ValidTelemetryEntity>();
                var pointId = te.PointId.GetString();
                if (string.IsNullOrEmpty(pointId)) continue;

                var data = new ValidTelemetryData
                {
                    PointId  = pointId,
                    Building = te.Building.GetString(),
                    DeviceId = te.DeviceId.GetString(),
                    Name     = te.Name.GetString(),
                    Datetime = te.Datetime.GetString(),
                };
                // Discriminated value (#152): number → Value, string → ValueText, boolean → ValueBool.
                TelemetryValueKind.Apply(data, te.Value.AsJsonElement);
                await hot.PutAsync(pointId, data, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ValidatedTelemetryHotStore: KV put failed");
        }
    }
}
