using BuildingOS.Shared.Infrastructure.ConnectorWorker;
using System.Text.Json;

namespace BuildingOS.Shared.Test.Infrastructure.ConnectorWorker;

public class IngressEnvelopeTest
{
    private static readonly JsonSerializerOptions Opts = new(JsonSerializerDefaults.Web);

    // ── Roundtrip tests ──────────────────────────────────────────────────────

    [Fact]
    public void Serialize_MqttEnvelope_RoundtripsAllFields()
    {
        var payload = JsonDocument.Parse("""{"value":23.5}""").RootElement.Clone();
        var receivedAt = new DateTimeOffset(2025, 1, 15, 12, 0, 0, TimeSpan.Zero);
        var original = new IngressEnvelope("telemetry/t1/d1", "t1", "d1", payload, receivedAt);

        var json = JsonSerializer.Serialize(original);
        var restored = JsonSerializer.Deserialize<IngressEnvelope>(json)!;

        Assert.Equal(original.Topic, restored.Topic);
        Assert.Equal(original.Tenant, restored.Tenant);
        Assert.Equal(original.DeviceId, restored.DeviceId);
        Assert.Equal(original.ReceivedAt, restored.ReceivedAt);
        Assert.Null(restored.MessageId);
        Assert.Equal("""{"value":23.5}""", restored.Payload.GetRawText());
    }

    [Fact]
    public void Serialize_AmqpEnvelope_RoundtripsMessageId()
    {
        var payload = JsonDocument.Parse("""{"value":42.0}""").RootElement.Clone();
        var receivedAt = new DateTimeOffset(2025, 6, 1, 9, 30, 0, TimeSpan.Zero);
        var original = new IngressEnvelope("/telemetry/t2", "t2", "dev-99", payload, receivedAt, "amqp-msg-001");

        var json = JsonSerializer.Serialize(original);
        var restored = JsonSerializer.Deserialize<IngressEnvelope>(json)!;

        Assert.Equal("amqp-msg-001", restored.MessageId);
        Assert.Equal("dev-99", restored.DeviceId);
        Assert.Equal(42.0, restored.Payload.GetProperty("value").GetDouble());
    }

    // ── Regression: wrong field name must NOT silently populate the property ─

    [Fact]
    public void Deserialize_WrongFieldName_ReceivedAtIsDefault()
    {
        // "received_at" (snake_case) instead of "receivedAt" (camelCase) — must not match
        const string badJson = """
            {
              "topic": "t",
              "tenant": "x",
              "deviceId": "d",
              "payload": {},
              "received_at": "2025-01-01T00:00:00Z"
            }
            """;

        var envelope = JsonSerializer.Deserialize<IngressEnvelope>(badJson)!;

        Assert.Equal(DateTimeOffset.MinValue, envelope.ReceivedAt);
    }

    [Fact]
    public void Deserialize_WrongFieldName_DeviceIdIsEmpty()
    {
        // "device_id" instead of "deviceId" — must not match
        const string badJson = """
            {
              "topic": "t",
              "tenant": "x",
              "device_id": "sensor-1",
              "payload": {},
              "receivedAt": "2025-01-01T00:00:00Z"
            }
            """;

        var envelope = JsonSerializer.Deserialize<IngressEnvelope>(badJson)!;

        Assert.Null(envelope.DeviceId);
    }

    // ── Serialized field names match the wire contract ────────────────────────

    [Fact]
    public void Serialize_UsesCorrectWireFieldNames()
    {
        var payload = JsonDocument.Parse("{}").RootElement.Clone();
        var envelope = new IngressEnvelope("topic", "tenant", "deviceId", payload,
            new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero), "msgId");

        var doc = JsonDocument.Parse(JsonSerializer.Serialize(envelope));
        var root = doc.RootElement;

        Assert.True(root.TryGetProperty("topic", out _), "missing 'topic'");
        Assert.True(root.TryGetProperty("tenant", out _), "missing 'tenant'");
        Assert.True(root.TryGetProperty("deviceId", out _), "missing 'deviceId'");
        Assert.True(root.TryGetProperty("payload", out _), "missing 'payload'");
        Assert.True(root.TryGetProperty("receivedAt", out _), "missing 'receivedAt'");
        Assert.True(root.TryGetProperty("messageId", out _), "missing 'messageId'");

        // must NOT serialize under wrong names
        Assert.False(root.TryGetProperty("device_id", out _), "unexpected 'device_id'");
        Assert.False(root.TryGetProperty("received_at", out _), "unexpected 'received_at'");
    }
}
