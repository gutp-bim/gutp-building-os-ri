using BuildingOS.Shared.Entities;
using BuildingOS.Shared.Helpers;
using BuildingOS.Shared.Infrastructure.ConnectorWorker;
using BuildingOS.Shared.Infrastructure.Messaging;
using BuildingOS.Shared.Module;
using Corvus.Json;
using System.Text.Json;

namespace BuildingOS.ConnectorWorker.Connectors;

/// <summary>
/// Base class for IoT ingress connectors that receive messages via transport workers
/// (MqttIngressWorker, AmqpIngressWorker) and process the resulting envelope.
///
/// Envelope format (from any ingress worker): IngressEnvelope
///   { "topic": "...", "tenant": "...", "deviceId": "...",
///     "payload": { ... }, "receivedAt": "...", "messageId": "..." }
///
/// ProcessAsync behavior:
///   1. If payload is already valid ValidMessageJson → passthrough directly to validated.telemetry
///   2. Otherwise → resolve pointId via IPointIdFactory and build validated telemetry
/// </summary>
public abstract class IoTIngressConnectorBase(
    IMessageSubscription subscription,
    INatsPublisher publisher,
    IPointIdFactory pointIdFactory,
    string protocolTag,
    ILogger logger)
    : ConnectorWorkerBase(subscription, publisher, "building-os.validated.telemetry", logger)
{
    protected override async Task<string?> ProcessAsync(string rawMessage, CancellationToken cancellationToken)
    {
        IngressEnvelope? envelope;
        try { envelope = JsonSerializer.Deserialize<IngressEnvelope>(rawMessage); }
        catch (JsonException) { envelope = null; }

        if (envelope is null || string.IsNullOrEmpty(envelope.DeviceId))
        {
            logger.LogWarning("{Worker}: malformed envelope, skipping", GetType().Name);
            return null;
        }

        var tenant = string.IsNullOrEmpty(envelope.Tenant) ? "default" : envelope.Tenant;
        var payloadText = envelope.Payload.GetRawText();

        // Passthrough: device sent a pre-validated telemetry message directly
        var parsed = ValidMessageJson.Parse(payloadText);
        if (parsed.IsValid())
        {
            logger.LogDebug("{Worker}: validated passthrough for {Tenant}/{DeviceId}", GetType().Name, tenant, envelope.DeviceId);
            return payloadText;
        }

        // Standard path: extract numeric value and resolve pointId
        if (!envelope.Payload.TryGetProperty("value", out var valueProp) ||
            valueProp.ValueKind != JsonValueKind.Number)
        {
            logger.LogDebug("{Worker}: no numeric value in payload for {Tenant}/{DeviceId}", GetType().Name, tenant, envelope.DeviceId);
            return null;
        }

        var numericValue = valueProp.GetDouble();
        var localId = $"{tenant}/{envelope.DeviceId}";
        var (found, pointIds) = await pointIdFactory.TryGetPointIdAsync(protocolTag, localId);
        if (!found)
        {
            logger.LogDebug("{Worker}: pointId not found for localId={LocalId}", GetType().Name, localId);
            return null;
        }

        var timestamp = ExtractTimestamp(envelope.Payload, envelope.ReceivedAt);
        var pointId = pointIds.First();
        var epoch = DateTime.UtcNow.ToUnixTime();

        var entity = ValidMessageJson.ValidTelemetryEntity.Create(
            id: $"{pointId}.{epoch}",
            pointId: pointId,
            building: new JsonString(string.Empty),
            datetime: new JsonString(timestamp).As<JsonDateTime>(),
            value: (JsonNumber)numericValue,
            deviceId: envelope.DeviceId,
            name: envelope.DeviceId,
            data: new ValidMessageJson.ValidTelemetryEntity.DataEntity([
                new JsonObjectProperty("tenant", new JsonString(tenant)),
                new JsonObjectProperty("protocol", new JsonString(protocolTag))
            ]));

        return ValidMessageJson.Create(
            new ValidMessageJson.ValidTelemetryEntityArray([entity.AsAny])).ToString();
    }

    private static string ExtractTimestamp(JsonElement payload, DateTimeOffset receivedAt)
    {
        if (payload.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.String)
        {
            var raw = ts.GetString();
            if (raw != null && DateTimeOffset.TryParse(raw, out var parsed))
                return parsed.ToString("O");
        }

        // Fall back to envelope receivedAt; use UtcNow only if receivedAt was not set
        return (receivedAt != DateTimeOffset.MinValue ? receivedAt : DateTimeOffset.UtcNow).ToString("O");
    }
}
