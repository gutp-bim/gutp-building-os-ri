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
        Assert.True(ValidMessage.Parse(published.Message).IsValid());

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

    // ── Multi-gateway concurrency (#114) ──────────────────────────────────────
    // GatewayIngress pods are stateless (CLAUDE.md), so concurrent client streams from distinct
    // gateways are expected to be served by concurrent service instances sharing the singleton
    // bus/cache. These tests assert that concurrency does not leak/mix telemetry between gateways.

    [Fact]
    public async Task StreamTelemetry_TwoGatewaysConcurrently_NoCrossContamination()
    {
        var bus = new FakeIngressTelemetryBus();
        var cache = new FakePointMetadataCache(
            new PointMetadata("PT001", "bldg-1", "Room Temp", "DEV001", "GW001"),
            new PointMetadata("PT002", "bldg-2", "Damper Pos", "DEV002", "GW002"));
        const int framesPerGateway = 20;

        var readerA = new FakeStreamReader<TelemetryFrame>();
        var readerB = new FakeStreamReader<TelemetryFrame>();
        for (var i = 0; i < framesPerGateway; i++)
        {
            readerA.Push(new TelemetryFrame { GatewayId = "GW001", PointId = "PT001", Value = i });
            readerB.Push(new TelemetryFrame { GatewayId = "GW002", PointId = "PT002", Value = i });
        }
        readerA.Complete();
        readerB.Complete();

        // Two independent service instances (as gRPC would construct per-call) sharing the bus/cache.
        // Every fake dependency here (Channel-backed reader, ConcurrentQueue-backed bus, dictionary
        // cache) completes synchronously, so RunAsync would never yield the calling thread if awaited
        // directly — Task.Run forces each onto its own thread-pool thread so the two streams genuinely
        // race on the shared bus/cache instead of the test merely proving sequential-call correctness.
        var taskA = Task.Run(() => NewService(bus, cache).RunAsync(readerA, CancellationToken.None, trustedGatewayId: "GW001"));
        var taskB = Task.Run(() => NewService(bus, cache).RunAsync(readerB, CancellationToken.None, trustedGatewayId: "GW002"));
        var accepted = await Task.WhenAll(taskA, taskB);

        Assert.Equal(framesPerGateway, accepted[0]);
        Assert.Equal(framesPerGateway, accepted[1]);
        Assert.Equal(framesPerGateway * 2, bus.Published.Count);

        var gatewayIdsByPublishedPoint = bus.Published
            .Select(p =>
            {
                using var doc = JsonDocument.Parse(p.Message);
                var entity = doc.RootElement.GetProperty("telemetries")[0];
                return (PointId: entity.GetProperty("point_id").GetString(),
                    GatewayId: entity.GetProperty("data").GetProperty("gatewayId").GetString());
            })
            .ToArray();

        // Every frame attributed to GW001 must carry PT001 (its own point) and never PT002, and vice versa.
        Assert.All(gatewayIdsByPublishedPoint, e => Assert.Equal(e.GatewayId == "GW001" ? "PT001" : "PT002", e.PointId));
        Assert.Equal(framesPerGateway, gatewayIdsByPublishedPoint.Count(e => e.GatewayId == "GW001"));
        Assert.Equal(framesPerGateway, gatewayIdsByPublishedPoint.Count(e => e.GatewayId == "GW002"));
    }

    [Fact]
    public async Task StreamTelemetry_TwoGatewaysConcurrently_IdentityEnforced_SpoofAttemptIsolatedFromLegitStream()
    {
        // A concurrently-connected legitimate GW002 stream must keep publishing while a separate
        // connection attempts to spoof frames as GW001 without the matching trusted identity.
        var bus = new FakeIngressTelemetryBus();
        var cache = new FakePointMetadataCache(
            new PointMetadata("PT001", "bldg-1", "Room Temp", "DEV001", "GW001"),
            new PointMetadata("PT002", "bldg-2", "Damper Pos", "DEV002", "GW002"));

        var legitReader = new FakeStreamReader<TelemetryFrame>();
        legitReader.Push(new TelemetryFrame { GatewayId = "GW002", PointId = "PT002", Value = 1.0 });
        legitReader.Complete();

        var spoofReader = new FakeStreamReader<TelemetryFrame>();
        spoofReader.Push(new TelemetryFrame { GatewayId = "GW001", PointId = "PT001", Value = 1.0 }); // claims GW001
        spoofReader.Complete();

        var identity = new IngressIdentityOptions { Enforce = true };
        // See the Task.Run rationale on the sibling concurrency test above.
        var legitTask = Task.Run(() => NewService(bus, cache, identity).RunAsync(legitReader, CancellationToken.None, trustedGatewayId: "GW002"));
        var spoofTask = Task.Run(() => NewService(bus, cache, identity).RunAsync(spoofReader, CancellationToken.None, trustedGatewayId: "GW-ATTACKER"));
        var accepted = await Task.WhenAll(legitTask, spoofTask);

        Assert.Equal(1L, accepted[0]);
        Assert.Equal(0L, accepted[1]);
        var published = Assert.Single(bus.Published);
        using var doc = JsonDocument.Parse(published.Message);
        Assert.Equal("GW002", doc.RootElement.GetProperty("telemetries")[0].GetProperty("data").GetProperty("gatewayId").GetString());
    }

    [Fact]
    public async Task StreamTelemetry_ThreeGatewaysConcurrently_NoCrossContamination()
    {
        // Extends the two-gateway concurrency test above to three simultaneous streams, matching the
        // depth of GatewayEgress's Command_RoutesCorrectly_WithThreeConcurrentGateways (#114) — the
        // original issue's own gap analysis called out Ingress as having zero multi-gateway coverage,
        // and the fix landed with only two; this brings it to parity with Egress's three.
        var bus = new FakeIngressTelemetryBus();
        var gatewayIds = new[] { "GW001", "GW002", "GW003" };
        var pointIds = new[] { "PT001", "PT002", "PT003" };
        var cache = new FakePointMetadataCache(
            new PointMetadata(pointIds[0], "bldg-1", "Room Temp", "DEV001", gatewayIds[0]),
            new PointMetadata(pointIds[1], "bldg-2", "Damper Pos", "DEV002", gatewayIds[1]),
            new PointMetadata(pointIds[2], "bldg-3", "CO2 Level", "DEV003", gatewayIds[2]));
        const int framesPerGateway = 15;

        var readers = gatewayIds.Select(_ => new FakeStreamReader<TelemetryFrame>()).ToArray();
        for (var g = 0; g < gatewayIds.Length; g++)
        {
            for (var i = 0; i < framesPerGateway; i++)
                readers[g].Push(new TelemetryFrame { GatewayId = gatewayIds[g], PointId = pointIds[g], Value = i });
            readers[g].Complete();
        }

        // See the Task.Run rationale on the two-gateway concurrency test above.
        var tasks = new Task<long>[gatewayIds.Length];
        for (var g = 0; g < gatewayIds.Length; g++)
        {
            var idx = g;
            tasks[g] = Task.Run(() => NewService(bus, cache).RunAsync(readers[idx], CancellationToken.None, trustedGatewayId: gatewayIds[idx]));
        }
        var accepted = await Task.WhenAll(tasks);

        Assert.All(accepted, a => Assert.Equal(framesPerGateway, a));
        Assert.Equal(framesPerGateway * gatewayIds.Length, bus.Published.Count);

        var byGateway = bus.Published
            .Select(p =>
            {
                using var doc = JsonDocument.Parse(p.Message);
                var entity = doc.RootElement.GetProperty("telemetries")[0];
                return (PointId: entity.GetProperty("point_id").GetString(),
                    GatewayId: entity.GetProperty("data").GetProperty("gatewayId").GetString());
            })
            .ToArray();

        for (var g = 0; g < gatewayIds.Length; g++)
        {
            var forThisGateway = byGateway.Where(e => e.GatewayId == gatewayIds[g]).ToArray();
            Assert.Equal(framesPerGateway, forThisGateway.Length);
            Assert.All(forThisGateway, e => Assert.Equal(pointIds[g], e.PointId));
        }
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
