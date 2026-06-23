using System.Collections.Concurrent;
using System.Text.Json;
using BuildingOS.ConnectorWorker.Connectors;
using BuildingOS.ConnectorWorker.Protos;
using BuildingOS.Shared.Entities;
using BuildingOS.Shared.Module;
using BuildingOS.Shared.Test.Infrastructure.ConnectorWorker.Fakes;
using Corvus.Json;
using Microsoft.Extensions.Logging.Abstractions;

namespace BuildingOS.Shared.Test.Infrastructure.ConnectorWorker;

public class GatewayIngressServiceTest
{
    private const string Validated = "building-os.validated.telemetry";

    private static GatewayIngressService NewService(
        FakeIngressTelemetryBus bus, FakePointMetadataCache cache, IngressIdentityOptions? identity = null)
        => new(bus, cache, identity ?? new IngressIdentityOptions(), NullLogger<GatewayIngressService>.Instance);

    [Fact]
    public async Task StreamTelemetry_KnownPoint_PublishesEnrichedValidatedTelemetry()
    {
        var bus = new FakeIngressTelemetryBus();
        var cache = new FakePointMetadataCache(
            new PointMetadata("PT001", Building: "bldg-1", Name: "Room Temp", DeviceId: "DEV001", GatewayId: "GW001"));
        var reader = new FakeStreamReader<TelemetryFrame>();
        reader.Push(new TelemetryFrame { GatewayId = "GW001", PointId = "PT001", Value = 23.5, Timestamp = "2025-01-15T12:00:00Z" });
        reader.Complete();

        var accepted = await NewService(bus, cache).RunAsync(reader, CancellationToken.None);

        Assert.Equal(1L, accepted);
        var published = Assert.Single(bus.Published);
        Assert.Equal(Validated, published.Subject);
        Assert.True(ValidMessageJson.Parse(published.Message).IsValid());

        using var doc = JsonDocument.Parse(published.Message);
        var entity = doc.RootElement.GetProperty("telemetries")[0];
        Assert.Equal("PT001", entity.GetProperty("point_id").GetString());
        Assert.Equal(23.5, entity.GetProperty("value").GetDouble(), precision: 4);
        Assert.Equal("bldg-1", entity.GetProperty("building").GetString());
        Assert.Equal("GW001", entity.GetProperty("data").GetProperty("gatewayId").GetString());
    }

    [Fact]
    public async Task StreamTelemetry_Attributes_MergedIntoData()
    {
        var bus = new FakeIngressTelemetryBus();
        var cache = new FakePointMetadataCache(
            new PointMetadata("PT001", "bldg-1", "Room Temp", "DEV001", "GW001"));
        var reader = new FakeStreamReader<TelemetryFrame>();
        var frame = new TelemetryFrame { GatewayId = "GW001", PointId = "PT001", Value = 1.0 };
        frame.Attributes.Add("rawValue", "151.2");
        reader.Push(frame);
        reader.Complete();

        await NewService(bus, cache).RunAsync(reader, CancellationToken.None);

        using var doc = JsonDocument.Parse(Assert.Single(bus.Published).Message);
        var data = doc.RootElement.GetProperty("telemetries")[0].GetProperty("data");
        Assert.Equal("151.2", data.GetProperty("rawValue").GetString());
        Assert.Equal("GW001", data.GetProperty("gatewayId").GetString());
    }

    [Fact]
    public async Task StreamTelemetry_AttributeNamedGatewayId_DoesNotShadowProvenance()
    {
        var bus = new FakeIngressTelemetryBus();
        var cache = new FakePointMetadataCache(
            new PointMetadata("PT001", "bldg-1", "Room Temp", "DEV001", "GW001"));
        var reader = new FakeStreamReader<TelemetryFrame>();
        var frame = new TelemetryFrame { GatewayId = "GW001", PointId = "PT001", Value = 1.0 };
        frame.Attributes.Add("gatewayId", "SPOOFED"); // reserved key must not override provenance
        reader.Push(frame);
        reader.Complete();

        await NewService(bus, cache).RunAsync(reader, CancellationToken.None);

        using var doc = JsonDocument.Parse(Assert.Single(bus.Published).Message);
        var data = doc.RootElement.GetProperty("telemetries")[0].GetProperty("data");
        Assert.Equal("GW001", data.GetProperty("gatewayId").GetString());
    }

    [Fact]
    public async Task StreamTelemetry_UnknownPoint_Skipped()
    {
        var bus = new FakeIngressTelemetryBus();
        var cache = new FakePointMetadataCache(); // empty
        var reader = new FakeStreamReader<TelemetryFrame>();
        reader.Push(new TelemetryFrame { GatewayId = "GW001", PointId = "PT-NOPE", Value = 1.0 });
        reader.Complete();

        var accepted = await NewService(bus, cache).RunAsync(reader, CancellationToken.None);

        Assert.Equal(0L, accepted);
        Assert.Empty(bus.Published);
    }

    [Fact]
    public async Task StreamTelemetry_GatewayDoesNotOwnPoint_Skipped()
    {
        var bus = new FakeIngressTelemetryBus();
        var cache = new FakePointMetadataCache(
            new PointMetadata("PT001", "bldg-1", "Room Temp", "DEV001", GatewayId: "GW001"));
        var reader = new FakeStreamReader<TelemetryFrame>();
        reader.Push(new TelemetryFrame { GatewayId = "GW-OTHER", PointId = "PT001", Value = 1.0 });
        reader.Complete();

        var accepted = await NewService(bus, cache).RunAsync(reader, CancellationToken.None);

        Assert.Equal(0L, accepted);
        Assert.Empty(bus.Published);
    }

    [Fact]
    public async Task StreamTelemetry_TwinHasNoOwner_AllowedAsProvenanceOnly()
    {
        var bus = new FakeIngressTelemetryBus();
        var cache = new FakePointMetadataCache(
            new PointMetadata("PT001", "bldg-1", "Room Temp", "DEV001", GatewayId: ""));
        var reader = new FakeStreamReader<TelemetryFrame>();
        reader.Push(new TelemetryFrame { GatewayId = "GW-ANY", PointId = "PT001", Value = 1.0 });
        reader.Complete();

        var accepted = await NewService(bus, cache).RunAsync(reader, CancellationToken.None);

        Assert.Equal(1L, accepted);
        Assert.Single(bus.Published);
    }

    [Fact]
    public async Task StreamTelemetry_MissingIds_Skipped()
    {
        var bus = new FakeIngressTelemetryBus();
        var cache = new FakePointMetadataCache(
            new PointMetadata("PT001", "bldg-1", "Room Temp", "DEV001", "GW001"));
        var reader = new FakeStreamReader<TelemetryFrame>();
        reader.Push(new TelemetryFrame { GatewayId = "", PointId = "PT001", Value = 1.0 });       // no gateway
        reader.Push(new TelemetryFrame { GatewayId = "GW001", PointId = "", Value = 1.0 });        // no point
        reader.Complete();

        var accepted = await NewService(bus, cache).RunAsync(reader, CancellationToken.None);

        Assert.Equal(0L, accepted);
        Assert.Empty(bus.Published);
    }

    [Fact]
    public async Task StreamTelemetry_MixValidAndInvalid_CountsOnlyPublished()
    {
        var bus = new FakeIngressTelemetryBus();
        var cache = new FakePointMetadataCache(
            new PointMetadata("PT001", "bldg-1", "Room Temp", "DEV001", "GW001"));
        var reader = new FakeStreamReader<TelemetryFrame>();
        reader.Push(new TelemetryFrame { GatewayId = "GW001", PointId = "PT-NOPE", Value = 1.0 }); // unknown → skip
        reader.Push(new TelemetryFrame { GatewayId = "GW001", PointId = "PT001", Value = 2.0 });   // ok → publish
        reader.Complete();

        var accepted = await NewService(bus, cache).RunAsync(reader, CancellationToken.None);

        Assert.Equal(1L, accepted);
        Assert.Single(bus.Published);
    }

    [Fact]
    public async Task StreamTelemetry_PublishFails_NotAccepted_StreamContinues()
    {
        // #187: a JetStream publish-ack failure throws from the bus → the frame is not counted as
        // accepted, but the stream continues and a later good frame still publishes.
        var bus = new FakeIngressTelemetryBus { FailOnce = true };
        var cache = new FakePointMetadataCache(
            new PointMetadata("PT001", "bldg-1", "Room Temp", "DEV001", "GW001"));
        var reader = new FakeStreamReader<TelemetryFrame>();
        reader.Push(new TelemetryFrame { GatewayId = "GW001", PointId = "PT001", Value = 1.0 }); // publish throws
        reader.Push(new TelemetryFrame { GatewayId = "GW001", PointId = "PT001", Value = 2.0 }); // succeeds
        reader.Complete();

        var accepted = await NewService(bus, cache).RunAsync(reader, CancellationToken.None);

        Assert.Equal(1L, accepted);
        Assert.Single(bus.Published);
    }

    [Fact]
    public async Task StreamTelemetry_ReturnsZero_ForEmptyStream()
    {
        var bus = new FakeIngressTelemetryBus();
        var cache = new FakePointMetadataCache();
        var reader = new FakeStreamReader<TelemetryFrame>();
        reader.Complete();

        Assert.Equal(0L, await NewService(bus, cache).RunAsync(reader, CancellationToken.None));
        Assert.Empty(bus.Published);
    }

    // ── Identity binding (#296) ───────────────────────────────────────────────

    [Fact]
    public async Task IdentityEnforced_TrustedMatches_Publishes()
    {
        var bus = new FakeIngressTelemetryBus();
        var cache = new FakePointMetadataCache(
            new PointMetadata("PT001", "bldg-1", "Room Temp", "DEV001", "GW001"));
        var reader = new FakeStreamReader<TelemetryFrame>();
        reader.Push(new TelemetryFrame { GatewayId = "GW001", PointId = "PT001", Value = 1.0 });
        reader.Complete();

        var svc = NewService(bus, cache, new IngressIdentityOptions { Enforce = true });
        var accepted = await svc.RunAsync(reader, CancellationToken.None, trustedGatewayId: "GW001");

        Assert.Equal(1L, accepted);
        Assert.Single(bus.Published);
    }

    [Fact]
    public async Task IdentityEnforced_TrustedMismatch_Skipped()
    {
        var bus = new FakeIngressTelemetryBus();
        var cache = new FakePointMetadataCache(
            new PointMetadata("PT001", "bldg-1", "Room Temp", "DEV001", "GW001"));
        var reader = new FakeStreamReader<TelemetryFrame>();
        // Frame claims GW001 (which owns PT001) but the mTLS-verified identity is a different gateway.
        reader.Push(new TelemetryFrame { GatewayId = "GW001", PointId = "PT001", Value = 1.0 });
        reader.Complete();

        var svc = NewService(bus, cache, new IngressIdentityOptions { Enforce = true });
        var accepted = await svc.RunAsync(reader, CancellationToken.None, trustedGatewayId: "GW-ATTACKER");

        Assert.Equal(0L, accepted);
        Assert.Empty(bus.Published);
    }

    [Fact]
    public async Task IdentityEnforced_TrustedMissing_Skipped()
    {
        var bus = new FakeIngressTelemetryBus();
        var cache = new FakePointMetadataCache(
            new PointMetadata("PT001", "bldg-1", "Room Temp", "DEV001", "GW001"));
        var reader = new FakeStreamReader<TelemetryFrame>();
        reader.Push(new TelemetryFrame { GatewayId = "GW001", PointId = "PT001", Value = 1.0 });
        reader.Complete();

        var svc = NewService(bus, cache, new IngressIdentityOptions { Enforce = true });
        var accepted = await svc.RunAsync(reader, CancellationToken.None, trustedGatewayId: null);

        Assert.Equal(0L, accepted);
        Assert.Empty(bus.Published);
    }

    [Fact]
    public async Task IdentityNotEnforced_TrustedMismatch_StillPublishes()
    {
        // Back-compat: enforcement off → the trusted header is ignored (provenance-only).
        var bus = new FakeIngressTelemetryBus();
        var cache = new FakePointMetadataCache(
            new PointMetadata("PT001", "bldg-1", "Room Temp", "DEV001", "GW001"));
        var reader = new FakeStreamReader<TelemetryFrame>();
        reader.Push(new TelemetryFrame { GatewayId = "GW001", PointId = "PT001", Value = 1.0 });
        reader.Complete();

        var svc = NewService(bus, cache, new IngressIdentityOptions { Enforce = false });
        var accepted = await svc.RunAsync(reader, CancellationToken.None, trustedGatewayId: "GW-ANY");

        Assert.Equal(1L, accepted);
        Assert.Single(bus.Published);
    }

    [Theory]
    [InlineData("x-gateway-id", "GW001", "X-Gateway-Id", "GW001")]   // gRPC lower-cases keys; match is case-insensitive
    [InlineData("X-Gateway-Id", "  GW001 ", "X-Gateway-Id", "GW001")] // trimmed
    [InlineData("other", "GW001", "X-Gateway-Id", null)]               // header absent
    [InlineData("x-gateway-id", "   ", "X-Gateway-Id", null)]          // blank → null
    public void ResolveTrustedGatewayId_ReadsTrustedHeader(string key, string value, string headerName, string? expected)
    {
        var headers = new Grpc.Core.Metadata { { key, value } };
        Assert.Equal(expected, GatewayIngressService.ResolveTrustedGatewayId(headers, headerName));
    }

    [Fact]
    public void ResolveTrustedGatewayId_DuplicateDistinctValues_FailsClosed()
    {
        // A second X-Gateway-Id (ingress appended instead of strip+set, or a client smuggled one)
        // must not be trusted first-wins → return null so the frame is rejected under enforcement.
        var headers = new Grpc.Core.Metadata { { "x-gateway-id", "GW-ATTACKER" }, { "x-gateway-id", "GW-REAL" } };
        Assert.Null(GatewayIngressService.ResolveTrustedGatewayId(headers, "X-Gateway-Id"));
    }

    [Fact]
    public void ResolveTrustedGatewayId_DuplicateSameValue_Resolves()
    {
        var headers = new Grpc.Core.Metadata { { "x-gateway-id", "GW001" }, { "x-gateway-id", "GW001" } };
        Assert.Equal("GW001", GatewayIngressService.ResolveTrustedGatewayId(headers, "X-Gateway-Id"));
    }

    private sealed class FakeIngressTelemetryBus : IIngressTelemetryBus
    {
        private readonly ConcurrentQueue<(string Subject, string Message)> _published = new();
        public IReadOnlyList<(string Subject, string Message)> Published => _published.ToArray();

        /// <summary>When true, the next publish throws (simulates a JetStream publish-ack failure).</summary>
        public bool FailOnce { get; set; }

        public Task PublishAsync(string subject, string message, CancellationToken cancellationToken)
        {
            if (FailOnce)
            {
                FailOnce = false;
                throw new InvalidOperationException("simulated publish-ack failure");
            }
            _published.Enqueue((subject, message));
            return Task.CompletedTask;
        }
    }

    private sealed class FakePointMetadataCache(params PointMetadata[] points) : IPointMetadataCache
    {
        private readonly Dictionary<string, PointMetadata> _byPointId =
            points.ToDictionary(p => p.PointId, StringComparer.Ordinal);

        public Task<PointMetadata?> GetAsync(string pointId, CancellationToken cancellationToken = default)
            => Task.FromResult(_byPointId.TryGetValue(pointId, out var m) ? m : null);
    }
}
