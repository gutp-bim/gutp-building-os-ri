using BuildingOS.ConnectorWorker.Connectors;

namespace BuildingOS.Shared.Test.Infrastructure.ConnectorWorker;

/// <summary>
/// #415: MqttIngressWorker used to drop a malformed topic or a non-JSON payload with nothing but a
/// warning log — a genuine silent drop inside Building OS, invisible to any metric. The decision is
/// now a pure function so each outcome is testable and can be counted on
/// <c>building_os.ingress.messages</c> via its <c>result</c> tag.
/// </summary>
public class MqttIngressMessageClassifierTest
{
    [Fact]
    public void Classify_WellFormedTopicAndJson_IsPublishedWithTenantAndDevice()
    {
        var classified = MqttIngressMessageClassifier.Classify("telemetry/t1/dev-1", """{"value": 23.5}""");

        Assert.Equal(IngressTransportResults.Published, classified.Result);
        Assert.Equal("t1", classified.Tenant);
        Assert.Equal("dev-1", classified.DeviceId);
        Assert.Equal(23.5, classified.Payload.GetProperty("value").GetDouble());
    }

    [Theory]
    [InlineData("telemetry/t1")]   // deviceId missing
    [InlineData("telemetry")]      // tenant and deviceId missing
    [InlineData("telemetry//dev")] // empty tenant segment
    public void Classify_TopicMissingTenantOrDevice_IsBadTopic(string topic)
    {
        var classified = MqttIngressMessageClassifier.Classify(topic, """{"value": 1}""");

        Assert.Equal(IngressTransportResults.BadTopic, classified.Result);
    }

    [Fact]
    public void Classify_NonJsonPayload_IsBadPayload()
    {
        var classified = MqttIngressMessageClassifier.Classify("telemetry/t1/dev-1", "23.5 degrees");

        Assert.Equal(IngressTransportResults.BadPayload, classified.Result);
    }

    [Fact]
    public void Classify_DeviceIdKeepsRemainingTopicSegments()
    {
        // The topic is split into three parts only, so a device id containing slashes survives intact
        // (unchanged from the pre-#415 inline behaviour).
        var classified = MqttIngressMessageClassifier.Classify("telemetry/t1/floor2/dev-1", """{"value": 1}""");

        Assert.Equal(IngressTransportResults.Published, classified.Result);
        Assert.Equal("floor2/dev-1", classified.DeviceId);
    }

    [Fact]
    public void PublishedResult_MatchesTheGrpcIngressVocabulary()
    {
        // GatewayIngressService already tags building_os.ingress.messages with result=published for an
        // accepted frame. The transports reuse that word so one instrument means one thing.
        Assert.Equal("published", IngressTransportResults.Published);
    }
}
