using BuildingOS.Shared.Entities;
using BuildingOS.Shared.Infrastructure.Telemetry;
using Microsoft.Extensions.Logging;

namespace BuildingOS.Shared.Infrastructure.Oss;

/// <summary>
/// Writes the latest value per point_id from a validated-telemetry message to the hot store. Shared by
/// the <see cref="NatsKvPublisher"/> decorator and the gRPC ingress bus so both keep the hot store in
/// sync identically. KV errors are caught and logged — they must never fail the telemetry publish.
/// Cancellation of <paramref name="cancellationToken"/> is not one of those errors: it stops the write
/// quietly instead of reporting a hot-store failure per entity on the way down.
///
/// #415: because every validated-telemetry producer converges here, this is also where the
/// end-to-end event-time lag (<c>building_os.ingress.event_lag</c>) is measured — from the
/// <c>datetime</c> the message already carries, so nothing has to be threaded down from the
/// producers. It is not free: the string is already read for the KV payload, but turning it into an
/// instant costs one <c>DateTimeOffset.TryParse</c> per entity on this hot path (see
/// <see cref="Telemetry.IngestLagRecorder.RecordEventLag"/>).
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
                // Read outside the try so the handler below can name the point it lost. Still null
                // when the failure came before it was read — which is itself the diagnostic.
                string? pointId = null;
                try
                {
                    var te = entity.As<ValidMessage.ValidTelemetryEntity>();
                    pointId = te.PointId.GetString();
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
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // Shutdown, not a hot-store failure: stop quietly rather than logging one warning
                    // per remaining entity and going on writing to a store we are done with. Filtered
                    // on the token, so an OperationCanceledException raised for an unrelated reason
                    // (a KV client's own request timeout under saturation) still falls through to the
                    // warning below and leaves the per-entity isolation intact.
                    return;
                }
                catch (Exception ex)
                {
                    // This guards the whole per-entity path — projection and value mapping as well
                    // as the KV put — so it must not claim the put is what failed.
                    logger.LogWarning(
                        ex,
                        "ValidatedTelemetryHotStore: hot store sync failed for point {PointId} from source {Source}",
                        pointId,
                        source);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Same reason as the per-entity handler: a cancel is not unreadable telemetry.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "ValidatedTelemetryHotStore: validated telemetry unreadable for source {Source}", source);
        }
    }
}
