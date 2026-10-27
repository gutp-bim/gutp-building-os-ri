using Moq;
using Microsoft.Extensions.Logging.Abstractions;
using BuildingOS.Shared.Infrastructure.Telemetry;
using BuildingOS.Shared.Module;
using BuildingOS.Shared.Test.Infrastructure.ConnectorWorker.Fakes;
using System.Security.Cryptography;
using System.Text.Json;
using BuildingOS.ConnectorWorker.Connectors;
using BuildingOS.ConnectorWorker.Protos;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Crypto.Signers;

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
            var key = new Ed25519PrivateKeyParameters(RandomNumberGenerator.GetBytes(32), 0);
            var payload = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = "clock-run-context/2-draft", gatewayId = "GW-EXAMPLE", clockId = "urn:clock:test",
                provisioningRevision = "rev-a", runId = "run-a", generation = 1, status = "active",
                issuedAt = "2026-01-01T00:00:00Z", validFrom = "2026-01-01T00:00:00Z", expiresAt = "2026-01-01T01:00:00Z"
            });
            var signer = new Ed25519Signer(); signer.Init(true, key); signer.BlockUpdate(payload, 0, payload.Length);
            EnvelopePath = Path.Combine(DirectoryPath, "envelope.json");
            File.WriteAllBytes(EnvelopePath, JsonSerializer.SerializeToUtf8Bytes(new { payload, signature = signer.GenerateSignature() }));
            Config = Path.Combine(DirectoryPath, "trust.json");
            File.WriteAllBytes(Config, JsonSerializer.SerializeToUtf8Bytes(new[]
            {
                new { gatewayId = "GW-EXAMPLE", clockId = "urn:clock:test", publicKey = key.GeneratePublicKey().GetEncoded(), envelopeFile = EnvelopePath }
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
    public void ModifiedSignedFileIsRejectedAtLoad()
    {
        using var fixture = new Fixture();
        using var doc = JsonDocument.Parse(File.ReadAllBytes(fixture.EnvelopePath));
        var payload = doc.RootElement.GetProperty("payload").GetBytesFromBase64(); payload[0] ^= 1;
        var signature = doc.RootElement.GetProperty("signature").GetBytesFromBase64();
        File.WriteAllBytes(fixture.EnvelopePath, JsonSerializer.SerializeToUtf8Bytes(new { payload, signature }));
        Assert.Throws<InvalidOperationException>(() => RunContextVerifier.Load(fixture.Config));
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
}
