using System.Text.Json;

namespace BuildingOS.ConnectorWorker.Connectors;

/// <summary>
/// #415: decides whether an MQTT message can be forwarded to <c>building-os.raw.mqtt</c>, and if not,
/// why. Split out of <see cref="MqttIngressWorker"/> as a pure function so each outcome is unit
/// testable and can be counted on <c>building_os.ingress.messages</c> — before this, a malformed topic
/// or a non-JSON payload was dropped with nothing but a warning log, which is precisely the kind of
/// silent drop the reporter in #415 could not see from outside.
/// </summary>
internal static class MqttIngressMessageClassifier
{
    /// <summary>
    /// Classifies one message. The topic must be <c>telemetry/{tenant}/{deviceId}</c>; it is split into
    /// three parts only, so a device id containing slashes stays intact.
    /// </summary>
    internal static MqttIngressMessage Classify(string topic, string payloadText)
    {
        var parts = topic.Split('/', 3);
        var tenant = parts.Length > 1 ? parts[1] : string.Empty;
        var deviceId = parts.Length > 2 ? parts[2] : string.Empty;

        if (string.IsNullOrEmpty(tenant) || string.IsNullOrEmpty(deviceId))
            return new MqttIngressMessage(IngressTransportResults.BadTopic, tenant, deviceId, default);

        if (!TryParseJson(payloadText, out var payload))
            return new MqttIngressMessage(IngressTransportResults.BadPayload, tenant, deviceId, default);

        return new MqttIngressMessage(IngressTransportResults.Published, tenant, deviceId, payload);
    }

    private static bool TryParseJson(string text, out JsonElement element)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            element = doc.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            element = default;
            return false;
        }
    }
}

/// <summary>
/// Outcome of <see cref="MqttIngressMessageClassifier.Classify"/>. <see cref="Payload"/> is only
/// meaningful when <see cref="Result"/> is <see cref="IngressTransportResults.Published"/>.
/// </summary>
internal readonly record struct MqttIngressMessage(
    string Result, string Tenant, string DeviceId, JsonElement Payload)
{
    internal bool IsPublishable => Result == IngressTransportResults.Published;
}
