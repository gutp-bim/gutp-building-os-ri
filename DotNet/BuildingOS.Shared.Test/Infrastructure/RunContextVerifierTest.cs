using Moq;
using Microsoft.Extensions.Logging.Abstractions;
using BuildingOS.Shared.Infrastructure.Telemetry;
using BuildingOS.Shared.Module;
using BuildingOS.Shared.Test.Infrastructure.ConnectorWorker.Fakes;
using System.Text.Json;
using BuildingOS.ConnectorWorker.Connectors;
using BuildingOS.ConnectorWorker.Protos;

namespace BuildingOS.Shared.Test.Infrastructure;

public sealed class RunContextVerifierTest
{
    private sealed class Fixture : IDisposable
    {
        public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        public string Config { get; }
        public string EnvelopePath { get; }
        public TelemetryFrame Frame { get; }
        public Fixture()
        {
            Directory.CreateDirectory(DirectoryPath);
            var payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = "clock-run-context/2-draft", gatewayId = "GW-EXAMPLE", clockId = "urn:clock:test",
                provisioningRevision = "rev-a", runId = "run-a", generation = 1, status = "active",
                issuedAt = "2026-01-01T00:00:00Z", validFrom = "2026-01-01T00:00:00Z", expiresAt = "2026-01-01T01:00:00Z"
            });
            EnvelopePath = Path.Combine(DirectoryPath, "envelope.json");
            File.WriteAllBytes(EnvelopePath, JsonSerializer.SerializeToUtf8Bytes(new { payload }));
            Config = Path.Combine(DirectoryPath, "trust.json");
            File.WriteAllBytes(Config, JsonSerializer.SerializeToUtf8Bytes(new[]
            {
                new { gatewayId = "GW-EXAMPLE", clockId = "urn:clock:test", envelopeFile = EnvelopePath }
            }));
            Frame = new TelemetryFrame { GatewayId = "GW-EXAMPLE", PointId = "BPT-0002", Timestamp = "2026-01-01T00:00:05.000001Z" };
            foreach (var (name, value) in new Dictionary<string,string>
            {
                ["sim_clock_id"] = "urn:clock:test", ["sim_config_revision"] = "rev-a", ["sim_run_id"] = "run-a",
                ["sim_context_generation"] = "1", ["sim_run_status"] = "valid", ["sim_time_quality"] = "stable_unfenced",
                ["sim_time_s"] = "120.5", ["sim_poll_started_at"] = "2026-01-01T00:00:04Z",
                ["sim_poll_ended_at"] = "2026-01-01T00:00:06Z", ["sim_context_committed_at"] = "2026-01-01T00:00:01Z",
                ["sim_context_applied_at"] = "2026-01-01T00:00:02Z"
            }) Frame.Attributes[name] = value;

        }
        public void Dispose() => Directory.Delete(DirectoryPath, true);
    }

    [Fact]
    public void ValidContextRequiresTrustedGatewayAndPreservesAcquisitionTimestamp()
    {
        using var fixture = new Fixture(); var verifier = RunContextVerifier.Load(fixture.Config);
        Assert.True(verifier.Validate(fixture.Frame, "GW-EXAMPLE", out _));
        Assert.False(verifier.Validate(fixture.Frame, null, out _));
        Assert.False(verifier.Validate(fixture.Frame, "OTHER", out _));
        Assert.Equal("2026-01-01T00:00:05.000001Z", fixture.Frame.Timestamp);
    }

    [Theory]
    [InlineData("sim_run_id", "other")]
    [InlineData("sim_context_generation", "2")]
    [InlineData("sim_context_generation", "01")]
    [InlineData("sim_clock_id", "other")]
    [InlineData("sim_config_revision", "other")]
    [InlineData("sim_time_quality", "reset")]
    [InlineData("sim_time_s", "NaN")]
    [InlineData("sim_run_status", "running")]
    [InlineData("sim_run_status", "absent")]
    [InlineData("sim_run_reason", "unexpected")]
    [InlineData("sim_poll_started_at", null)]
    [InlineData("sim_poll_started_at", "2026-01-01T00:00:07Z")]
    [InlineData("sim_poll_ended_at", "2026-01-01T01:00:00Z")]
    [InlineData("sim_context_committed_at", "2025-12-31T23:59:59Z")]
    [InlineData("sim_context_applied_at", "2026-01-01T00:00:04.000000001Z")]
    public void InvalidClaimIsRejected(string key, string? value)
    {
        using var fixture = new Fixture(); var verifier = RunContextVerifier.Load(fixture.Config);
        if (value is null) fixture.Frame.Attributes.Remove(key); else fixture.Frame.Attributes[key] = value;
        Assert.False(verifier.Validate(fixture.Frame, "GW-EXAMPLE", out _));
    }

    [Fact]
    public async Task IngressRejectsUnboundRunBeforePublishingAndContinuesToValidFrame()
    {
        using var fixture = new Fixture(); var verifier = RunContextVerifier.Load(fixture.Config);
        var bus = new Mock<IIngressTelemetryBus>();
        bus.Setup(b => b.PublishAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var cache = new Mock<IPointMetadataCache>();
        cache.Setup(c => c.GetAsync("BPT-0002", It.IsAny<CancellationToken>())).ReturnsAsync(
            new PointMetadata("BPT-0002", "BLDG", "Temperature", "DEVICE", "GW-EXAMPLE"));
        var bad = fixture.Frame.Clone(); bad.Attributes["sim_run_id"] = "other";
        var reader = new FakeStreamReader<TelemetryFrame>(); reader.Push(bad); reader.Push(fixture.Frame); reader.Complete();
        var service = new GatewayIngressService(bus.Object, cache.Object, new IngressIdentityOptions { Enforce = true },
            new IngressHierarchyOptions(), NullLogger<GatewayIngressService>.Instance, verifier);
        Assert.Equal(1, await service.RunAsync(reader, CancellationToken.None, "GW-EXAMPLE"));
        bus.Verify(b => b.PublishAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private static (GatewayIngressService Service, Mock<IIngressTelemetryBus> Bus) NewService(RunContextVerifier? verifier)
    {
        var bus = new Mock<IIngressTelemetryBus>();
        bus.Setup(b => b.PublishAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var cache = new Mock<IPointMetadataCache>();
        cache.Setup(c => c.GetAsync("BPT-0002", It.IsAny<CancellationToken>())).ReturnsAsync(
            new PointMetadata("BPT-0002", "BLDG", "Temperature", "DEVICE", "GW-EXAMPLE"));
        return (new GatewayIngressService(bus.Object, cache.Object, new IngressIdentityOptions { Enforce = true },
            new IngressHierarchyOptions(), NullLogger<GatewayIngressService>.Instance, verifier), bus);
    }

    private static async Task<long> RunAsync(GatewayIngressService service, params TelemetryFrame[] frames)
    {
        var reader = new FakeStreamReader<TelemetryFrame>();
        foreach (var f in frames) reader.Push(f);
        reader.Complete();
        return await service.RunAsync(reader, CancellationToken.None, "GW-EXAMPLE");
    }

    [Fact]
    public async Task ConfiguredMatch_IsPublished()
    {
        using var fixture = new Fixture();
        var (service, bus) = NewService(RunContextVerifier.Load(fixture.Config));
        Assert.Equal(1, await RunAsync(service, fixture.Frame));
        bus.Verify(b => b.PublishAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ConfiguredMismatch_IsRejected()
    {
        using var fixture = new Fixture();
        var (service, bus) = NewService(RunContextVerifier.Load(fixture.Config));
        fixture.Frame.Attributes["sim_run_id"] = "other";
        Assert.Equal(0, await RunAsync(service, fixture.Frame));
        bus.Verify(b => b.PublishAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UnconfiguredClaim_FailsClosed()
    {
        using var fixture = new Fixture();
        var (service, bus) = NewService(null);
        Assert.Equal(0, await RunAsync(service, fixture.Frame));
        bus.Verify(b => b.PublishAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task UnconfiguredWithoutClaim_StillPublishes()
    {
        using var fixture = new Fixture();
        var plain = new TelemetryFrame { GatewayId = "GW-EXAMPLE", PointId = "BPT-0002", Timestamp = fixture.Frame.Timestamp };
        var absent = plain.Clone(); absent.Attributes["sim_run_status"] = "absent";
        var (service, bus) = NewService(null);
        Assert.Equal(2, await RunAsync(service, plain, absent));
        bus.Verify(b => b.PublishAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    [Fact]
    public void UnsignedEnvelopeWithExtraSignatureFieldIsRejectedAtLoad()
    {
        using var fixture = new Fixture();
        var payload = JsonDocument.Parse(File.ReadAllBytes(fixture.EnvelopePath)).RootElement.GetProperty("payload").GetBytesFromBase64();
        File.WriteAllBytes(fixture.EnvelopePath, JsonSerializer.SerializeToUtf8Bytes(new { payload, signature = new byte[64] }));
        Assert.Throws<JsonException>(() => RunContextVerifier.Load(fixture.Config));
    }
}
