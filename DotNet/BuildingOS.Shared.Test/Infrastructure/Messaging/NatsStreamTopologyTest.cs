using BuildingOS.Shared.Infrastructure.Messaging;

namespace BuildingOS.Shared.Test.Infrastructure.Messaging;

public class NatsStreamTopologyTest
{
    // ── Resolve ───────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("building-os.raw.hvac",          "BUILDING_OS_RAW")]
    [InlineData("building-os.raw.bacnet",         "BUILDING_OS_RAW")]
    [InlineData("building-os.raw.mqtt",           "BUILDING_OS_RAW")]
    [InlineData("building-os.validated.telemetry","BUILDING_OS_VALIDATED")]
    [InlineData("building-os.control.request",    "BUILDING_OS_CONTROL")]
    [InlineData("building-os.dlq.dead",           "BUILDING_OS_DLQ")]
    public void Resolve_KnownSubject_ReturnsExpectedStream(string subject, string expectedStream)
    {
        var (streamName, _) = NatsStreamTopology.Resolve(subject);
        Assert.Equal(expectedStream, streamName);
    }

    [Fact]
    public void Resolve_RawSubject_CapturesWildcard()
    {
        var (_, subjects) = NatsStreamTopology.Resolve("building-os.raw.hvac");
        Assert.Contains("building-os.raw.>", subjects);
    }

    /// <summary>
    /// #381: the control stream must bind ONLY the durable generic request subject. Binding
    /// <c>building-os.control.&gt;</c> makes JetStream register interest on the per-gateway egress
    /// subject and answer a NATS request with a PubAck, which permanently disables the
    /// no-responders check that <c>NatsPointControlCommandPublisher</c> uses to return 503 for an
    /// offline gateway (#186). The 503 branch becomes unreachable and the caller waits out the
    /// result timeout instead.
    /// </summary>
    [Theory]
    [InlineData("building-os.control.request.gw.GW-SOS-001")] // per-gateway egress (core NATS request)
    [InlineData("building-os.control.result.abc-123")]        // WaitForResult reply (core NATS)
    public void Resolve_ControlSubject_DoesNotCaptureCoreNatsRequestReplySubjects(string coreNatsSubject)
    {
        var (streamName, subjects) = NatsStreamTopology.Resolve("building-os.control.request");

        Assert.Equal("BUILDING_OS_CONTROL", streamName);
        Assert.Equal(["building-os.control.request"], subjects);
        // A literal subject matches token-for-token, so neither of these is captured.
        Assert.DoesNotContain(coreNatsSubject, subjects);
        Assert.DoesNotContain("building-os.control.>", subjects);
    }

    [Fact]
    public void Resolve_UnknownSubject_UsesFallback()
    {
        var (streamName, subjects) = NatsStreamTopology.Resolve("custom.subject.here");
        // Fallback: uppercased first segment
        Assert.Equal("CUSTOM", streamName);
        Assert.Contains("custom.subject.here", subjects);
    }

    // ── ResolveOrThrow ────────────────────────────────────────────────────────

    [Theory]
    [InlineData("building-os.raw.hvac")]
    [InlineData("building-os.raw.new-connector")]
    [InlineData("building-os.validated.telemetry")]
    [InlineData("building-os.control.request")]
    [InlineData("building-os.dlq.dead")]
    public void ResolveOrThrow_KnownSubject_DoesNotThrow(string subject)
    {
        var ex = Record.Exception(() => NatsStreamTopology.ResolveOrThrow(subject));
        Assert.Null(ex);
    }

    [Theory]
    [InlineData("unknown.subject")]
    [InlineData("building_os.raw.hvac")]   // underscore instead of hyphen
    [InlineData("buildingos.raw.hvac")]    // missing hyphen
    [InlineData("")]
    public void ResolveOrThrow_UnknownSubject_ThrowsInvalidOperation(string subject)
    {
        Assert.Throws<InvalidOperationException>(() =>
            NatsStreamTopology.ResolveOrThrow(subject));
    }

    [Fact]
    public void ResolveOrThrow_ErrorMessage_ContainsSubject()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            NatsStreamTopology.ResolveOrThrow("typo.raw.hvac"));
        Assert.Contains("typo.raw.hvac", ex.Message);
    }
}
