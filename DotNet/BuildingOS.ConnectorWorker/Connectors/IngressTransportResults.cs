namespace BuildingOS.ConnectorWorker.Connectors;

/// <summary>
/// #415: the <c>result</c> tag values the MQTT/AMQP transport ingress workers put on
/// <c>building_os.ingress.messages</c>, so a message they refuse is counted rather than only logged.
///
/// <see cref="Published"/> deliberately reuses the word <c>GatewayIngressService</c> already uses for
/// an accepted frame: one instrument, one vocabulary, and
/// <c>IngressRejectionStatsService</c> — which treats "published" as the single non-rejection value —
/// stays correct if its <c>source="gateway-grpc"</c> selector is ever widened. The refusal reasons are
/// transport-specific and are kept out of that view by that selector.
/// </summary>
internal static class IngressTransportResults
{
    /// <summary>Forwarded to the raw NATS subject.</summary>
    internal const string Published = "published";

    /// <summary>MQTT only: the topic did not carry both a tenant and a device id.</summary>
    internal const string BadTopic = "bad_topic";

    /// <summary>The payload was absent or not JSON.</summary>
    internal const string BadPayload = "bad_payload";
}
