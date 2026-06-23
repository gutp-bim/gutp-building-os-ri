using System.Text.Json;
using System.Text.Json.Serialization;

namespace BuildingOS.Shared.Infrastructure.ConnectorWorker;

/// <summary>
/// Typed wire format shared between ingress transport workers (MqttIngressWorker,
/// AmqpIngressWorker) and IoT connector workers (IoTIngressConnectorBase).
///
/// Using this record as the serialization contract means field-name changes are caught
/// at compile time rather than silently dropped as a null value at runtime.
/// </summary>
public record IngressEnvelope(
    [property: JsonPropertyName("topic")] string Topic,
    [property: JsonPropertyName("tenant")] string Tenant,
    [property: JsonPropertyName("deviceId")] string DeviceId,
    [property: JsonPropertyName("payload")] JsonElement Payload,
    [property: JsonPropertyName("receivedAt")] DateTimeOffset ReceivedAt,
    [property: JsonPropertyName("messageId")] string? MessageId = null);
