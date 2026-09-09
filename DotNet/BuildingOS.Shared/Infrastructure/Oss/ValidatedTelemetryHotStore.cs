using BuildingOS.Shared.Entities;
using BuildingOS.Shared.Infrastructure.Telemetry;
using Microsoft.Extensions.Logging;

namespace BuildingOS.Shared.Infrastructure.Oss;

/// <summary>
/// Writes the latest value per point_id from a validated-telemetry message to the hot store. Shared by
/// the <see cref="NatsKvPublisher"/> decorator and the gRPC ingress bus so both keep the hot store in
/// sync identically. KV errors are caught and logged — they must never fail the telemetry publish.
///
/// #415: because every validated-telemetry producer converges here, this is also where the
/// end-to-end event-time lag (<c>building_os.ingress.event_lag</c>) is measured — from the
/// <c>datetime</c> the message already carries, so the measurement costs no extra parse.
/// <paramref name="source"/> says which producer it came through (<c>connector</c> for the
/// NatsKvPublisher decorator, <c>gateway-grpc</c> for the ingress bus).
/// </summary>
public static class ValidatedTelemetryHotStore
{
    public static async Task WriteAsync(
        IHotTelemetryStore hot, string message, string source, ILogger logger, CancellationToken cancellationToken)
    {
        try
        {
            // Payload is ValidMessage: { "telemetries": [...] } with snake_case keys.
            // Iterate each entity and write the latest value per point_id to the KV store.
            var msg = ValidMessage.Parse(message);
            foreach (var entity in msg.Telemetries.EnumerateArray())
            {
                // Per-entity isolation, deliberately not one try/catch around the whole loop: a KV that
                // is failing (or timing out under saturation) is exactly the situation the lag metric
                // exists to expose, so one bad put must neither skip the remaining entities nor stop
                // them being measured.
                try
                {
                    var te = entity.As<ValidMessage.ValidTelemetryEntity>();
                    var pointId = te.PointId.GetString();
                    if (string.IsNullOrEmpty(pointId)) continue;

                    var datetime = te.Datetime.GetString();
                    // Recorded before the put, so a slow/failing KV cannot swallow the measurement.
                    IngestLagRecorder.RecordEventLag(source, datetime, DateTimeOffset.UtcNow);

                    var data = new ValidTelemetryData
                    {
                        PointId  = pointId,
                        Building = te.Building.GetString(),
                        DeviceId = te.DeviceId.GetString(),
                        Name     = te.Name.GetString(),
                        Datetime = datetime,
                    };
                    // Discriminated value (#152): number → Value, string → ValueText, boolean → ValueBool.
                    TelemetryValueKind.Apply(data, te.Value.AsJsonElement);
                    await hot.PutAsync(pointId, data, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "ValidatedTelemetryHotStore: KV put failed for source {Source}", source);
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ValidatedTelemetryHotStore: validated telemetry unreadable for source {Source}", source);
        }
    }
}
